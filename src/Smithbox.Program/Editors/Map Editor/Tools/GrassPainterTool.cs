using Hexa.NET.ImGui;
using Andre.Formats;
using SoulsFormats;
using StudioCore.Application;
using StudioCore.Editors.Common;
using StudioCore.Editors.Viewport;
using StudioCore.Keybinds;
using StudioCore.Renderer;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Veldrid.Utilities;

namespace StudioCore.Editors.MapEditor;

public class GrassPainterTool
{
    private sealed class GrassParamOption
    {
        public int ParamId { get; init; }
        public string DisplayLabel { get; init; }
        public string SearchLabel { get; init; }
        public bool HasTextures { get; init; }
    }

    private sealed class GrassPreviewSettings
    {
        public string ModelName { get; init; }
        public int SourceParamId { get; init; }
        public int ClusterType { get; init; }
        public float BaseDensity { get; init; }
        public float Spacing { get; init; }
        public float WidthScaleMin { get; init; }
        public float WidthScaleMax { get; init; }
        public float HeightScaleMin { get; init; }
        public float HeightScaleMax { get; init; }
        public float OrientationAngle { get; init; }
        public float OrientationRange { get; init; }
        public string Summary { get; init; }

        public int GetPreviewInstanceCount()
        {
            if (BaseDensity >= 3.0f)
            {
                return 4;
            }

            if (BaseDensity >= 1.75f)
            {
                return 3;
            }

            if (BaseDensity >= 1.1f)
            {
                return 2;
            }

            return 1;
        }
    }

    private enum GrassPaintOperation
    {
        PaintSlot,
        ApplyPalette,
        ClearSlot,
        ClearAll
    }

    private enum HoveredTargetState
    {
        None,
        GrassCapableMapPiece,
        GrassCapableAsset,
        FilteredOutMapPiece,
        FilteredOutAsset,
        UnsupportedDummyAsset,
        UnsupportedTarget
    }

    private static readonly string[] OperationLabels =
    [
        "Paint Selected Slot",
        "Apply Full Palette",
        "Clear Selected Slot",
        "Clear All Slots"
    ];

    private const int BrushPreviewMarkerCount = 13;
    private const int StrokeTrailCapacity = 24;

    private readonly MapEditorView View;
    private readonly ProjectEntry Project;
    private readonly int[] _paletteSlots = new int[6];
    private readonly HashSet<Entity> _strokeEntities = new();
    private readonly List<ViewportAction> _strokeActions = new();
    private readonly Dictionary<int, string> _grassParamNames = new();
    private string _grassParamSearch = "";
    private List<GrassParamOption> _cachedGrassParamOptions;
    private int _cachedGrassParamVersion = -1;

    private GrassPaintTarget _hoveredTarget;
    private Entity _hoveredEntity;
    private HoveredTargetState _hoveredTargetState;
    private GrassPaintTarget _lockedStrokeTarget;
    private Entity _lockedStrokeEntity;
    private HoveredTargetState _lockedStrokeTargetState;
    private DebugPrimitiveRenderableProxy _brushPreviewProxy;
    private readonly List<DebugPrimitiveRenderableProxy> _brushPreviewMarkers = new();
    private readonly List<DebugPrimitiveRenderableProxy> _strokeTrailMarkers = new();
    private readonly List<List<MeshRenderableProxy>> _strokeTrailGrassPreviewGroups = new();
    private readonly List<List<MeshRenderableProxy>> _committedGrassPreviewGroups = new();
    private readonly List<Vector3> _strokeTrailPositions = new();
    private readonly List<float> _strokeTrailScales = new();
    private readonly List<Vector3> _strokeTrailNormals = new();
    private RenderScene _brushPreviewScene;
    private string _strokeStatusMessage = "Select a non-zero grass param, then left-drag across a grass-capable target.";
    private Vector4 _strokeStatusColor = new(0.75f, 0.75f, 0.75f, 1.0f);
    private bool _strokeInProgress;
    private bool _paintEnabled;
    private bool _allowMapPieces = true;
    private bool _allowAssets = true;
    private int _selectedSlot;
    private int _paintGrassParamId;
    private GrassPaintOperation _operation;
    private readonly Dictionary<string, RaycastMeshData> _raycastMeshCache = new();

    private sealed class RaycastMeshData
    {
        public Vector3[] Vertices;
        public int[] Indices;
    }

    public GrassPainterTool(MapEditorView view, ProjectEntry project)
    {
        View = view;
        Project = project;
        TrySeedDefaultPaintValue();
    }

    public void OnToolWindow()
    {
        if (!ImGui.CollapsingHeader("Grass Painter", ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }

        if (!GrassPaintAdapter.SupportsProject(Project.Descriptor.ProjectType))
        {
            ImGui.TextWrapped("Viewport grass painting is currently supported for Elden Ring, Armored Core VI, and Nightreign projects.");
            return;
        }

        TrySeedDefaultPaintValue();

        if (ImGui.Checkbox("Enable Viewport Painting", ref _paintEnabled) && _paintEnabled)
        {
            TrySeedDefaultPaintValue();
        }
        ImGui.TextWrapped("When enabled, left drag in the active viewport applies the configured grass data to supported map pieces and assets.");
        ImGui.TextWrapped("This tool edits per-part grass slot data on grass-capable MSB parts. Empty grass-capable assets can be painted from scratch by assigning their first non-zero slot through this tool.");
        ImGui.TextWrapped("This does not author a separate terrain-density resource.");
        ImGui.TextWrapped("Viewport preview attempts to spawn temporary grass clumps from the selected GrassTypeParam model fields. Billboard- and flat-only grass still fall back to surface stamps.");

        var activePreviewSummary = GetActivePreviewSummary();
        if (!string.IsNullOrWhiteSpace(activePreviewSummary))
        {
            ImGui.TextWrapped($"Preview Source: {activePreviewSummary}");
        }

        var operationIndex = (int)_operation;
        if (ImGui.Combo("Stroke Operation", ref operationIndex, OperationLabels, OperationLabels.Length))
        {
            _operation = (GrassPaintOperation)operationIndex;
            if (_operation == GrassPaintOperation.PaintSlot)
            {
                _paintGrassParamId = _paletteSlots[_selectedSlot];
            }
        }

        if (_operation is GrassPaintOperation.PaintSlot or GrassPaintOperation.ClearSlot)
        {
            if (ImGui.SliderInt("Target Slot", ref _selectedSlot, 0, 5) && _operation == GrassPaintOperation.PaintSlot)
            {
                _paintGrassParamId = _paletteSlots[_selectedSlot];
            }
        }

        if (_operation is GrassPaintOperation.PaintSlot)
        {
            DrawGrassParamPicker();

            var paintGrassParamId = _paintGrassParamId;
            if (ImGui.InputInt("Paint Grass Param ID", ref paintGrassParamId))
            {
                SetPaintGrassParam(paintGrassParamId);
            }
        }

        ImGui.Separator();
        ImGui.Text("Palette Slots");
        for (var index = 0; index < _paletteSlots.Length; index++)
        {
            ImGui.PushID(index);
            var slotValue = _paletteSlots[index];
            if (ImGui.InputInt($"Slot {index}", ref slotValue))
            {
                _paletteSlots[index] = slotValue;
                if (index == _selectedSlot && _operation == GrassPaintOperation.PaintSlot)
                {
                    _paintGrassParamId = slotValue;
                }
            }

            ImGui.SameLine();
            ImGui.TextDisabled(GetGrassParamDisplay(slotValue));
            ImGui.PopID();
        }

        ImGui.Separator();
        ImGui.Checkbox("Affect Map Pieces", ref _allowMapPieces);
        ImGui.SameLine();
        ImGui.Checkbox("Affect Assets", ref _allowAssets);

        if (ImGui.Button("Load From Selection"))
        {
            LoadFromSelection();
        }

        ImGui.SameLine();
        if (ImGui.Button("Apply To Selection"))
        {
            ApplyToSelection();
        }

        if (_hoveredTarget != null)
        {
            ImGui.SameLine();
            if (ImGui.Button("Load Hovered"))
            {
                LoadPalette(_hoveredTarget.ReadSlots());
            }
        }

        if (_strokeInProgress)
        {
            ImGui.Text($"Pending stroke edits: {_strokeActions.Count}");
        }

        ImGui.TextColored(_strokeStatusColor, _strokeStatusMessage);

        if (_hoveredTarget != null)
        {
            ImGui.Separator();
            ImGui.Text($"Hovered Target: {_hoveredTarget.Entity.Name}");
            DrawHoveredStateLabel();
            var hoveredSlots = _hoveredTarget.ReadSlots();
            if (hoveredSlots.All(slot => slot == 0))
            {
                ImGui.TextColored(new Vector4(0.45f, 0.9f, 0.55f, 1.0f), "No grass assigned yet. Painting here will create a new grass assignment.");
            }
            for (var index = 0; index < hoveredSlots.Length; index++)
            {
                ImGui.Text($"Slot {index}: {GetGrassParamDisplay(hoveredSlots[index])}");
            }
        }
        else if (_hoveredEntity != null)
        {
            ImGui.Separator();
            ImGui.Text($"Hovered Target: {_hoveredEntity.Name}");
            DrawHoveredStateLabel();
        }
        else
        {
            ImGui.TextWrapped("Hover a supported terrain part in the viewport to inspect its current grass slots.");
        }
    }

    public void DrawViewportOverlay()
    {
        if (!IsViewportPaintingEnabled())
        {
            return;
        }

        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.45f, 0.9f, 0.55f, 1.0f), "Grass Painter Active");
        ImGui.Text($"Operation: {OperationLabels[(int)_operation]}");

        if (_operation is GrassPaintOperation.PaintSlot or GrassPaintOperation.ClearSlot)
        {
            ImGui.Text($"Target Slot: {_selectedSlot}");
        }

        if (_operation == GrassPaintOperation.PaintSlot)
        {
            ImGui.Text($"Paint Value: {GetGrassParamDisplay(_paintGrassParamId)}");
        }

        ImGui.Text($"Targets: {GetTargetFilterLabel()}");

        if (_strokeInProgress)
        {
            ImGui.Text($"Stroke Targets: {_strokeActions.Count}");
        }

        if (_strokeTrailPositions.Count > 0)
        {
            ImGui.Text($"Visible Stamps: {_strokeTrailPositions.Count}");
        }

        var committedMeshCount = 0;
        foreach (var g in _committedGrassPreviewGroups)
            committedMeshCount += g.Count;
        if (_committedGrassPreviewGroups.Count > 0)
        {
            ImGui.Text($"Committed Previews: {_committedGrassPreviewGroups.Count} groups, {committedMeshCount} meshes");
        }

        var activePreviewSummary = GetActivePreviewSummary();
        if (!string.IsNullOrWhiteSpace(activePreviewSummary))
        {
            ImGui.TextWrapped($"Preview: {activePreviewSummary}");
        }

        ImGui.TextColored(_strokeStatusColor, _strokeStatusMessage);

        if (_hoveredTarget != null)
        {
            ImGui.Text($"Hovered: {_hoveredTarget.Entity.Name}");
            DrawHoveredStateLabel();

            if (_hoveredTarget.ReadSlots().All(slot => slot == 0))
            {
                ImGui.TextColored(new Vector4(0.45f, 0.9f, 0.55f, 1.0f), "Hovered target has no grass yet; stroke will seed it.");
            }
        }
        else if (_hoveredEntity != null)
        {
            ImGui.Text($"Hovered: {_hoveredEntity.Name}");
            DrawHoveredStateLabel();
        }
        else
        {
            ImGui.TextDisabled("Hovered: no supported target");
        }
    }

    public bool HandleViewportInteraction(VulkanViewport viewport, out bool suppressCameraInput)
    {
        suppressCameraInput = false;

        if (!_paintEnabled || !GrassPaintAdapter.SupportsProject(Project.Descriptor.ProjectType))
        {
            if (_strokeInProgress)
            {
                CancelStroke();
            }

            _hoveredTarget = null;
            _hoveredEntity = null;
            _hoveredTargetState = HoveredTargetState.None;
            HideBrushPreview();
            return false;
        }

        if (_strokeInProgress && !InputManager.IsMouseDown(MousebindID.Viewport_Picking_Action))
        {
            CommitStroke();
        }

        if (!viewport.IsActiveViewport)
        {
            HideBrushPreview();
            return false;
        }

        if (!viewport.MouseInViewport())
        {
            HideBrushPreview();
            return _strokeInProgress;
        }

        if (_strokeInProgress && _lockedStrokeTarget != null)
        {
            RestoreLockedStrokeHover();
        }
        else
        {
            UpdateHoveredTarget(viewport);
        }

        UpdateBrushPreview(viewport);

        var isPaintingButtonDown = InputManager.IsMouseDown(MousebindID.Viewport_Picking_Action);
        var isViewportMovementDown = InputManager.IsMouseDown(MousebindID.Viewport_Enable_Viewport_Movement);

        if (!isViewportMovementDown && isPaintingButtonDown)
        {
            suppressCameraInput = true;

            if (!_strokeInProgress)
            {
                _strokeInProgress = true;
                _strokeEntities.Clear();
                _strokeActions.Clear();
                CommitStrokePreviewGroups();
                LockStrokeTarget();
            }

            PaintHoveredTarget();
        }

        return true;
    }

    private void UpdateHoveredTarget(VulkanViewport viewport)
    {
        viewport.ViewPipeline.CreateAsyncPickingRequest();

        if (!viewport.ViewPipeline.PickingResultsReady)
        {
            return;
        }

        _hoveredTarget = null;
        _hoveredEntity = null;
        _hoveredTargetState = HoveredTargetState.None;

        if (viewport.ViewPipeline.GetSelection() is Entity entity)
        {
            _hoveredEntity = entity;

            if (EntityHelper.IsPartDummyAsset(entity))
            {
                _hoveredTargetState = HoveredTargetState.UnsupportedDummyAsset;
                return;
            }

            if (GrassPaintAdapter.TryCreate(entity, out var target))
            {
                if (EntityHelper.IsPartMapPiece(entity))
                {
                    if (_allowMapPieces)
                    {
                        _hoveredTarget = target;
                        _hoveredTargetState = HoveredTargetState.GrassCapableMapPiece;
                    }
                    else
                    {
                        _hoveredTargetState = HoveredTargetState.FilteredOutMapPiece;
                    }

                    return;
                }

                if (EntityHelper.IsPartPureAsset(entity))
                {
                    if (_allowAssets)
                    {
                        _hoveredTarget = target;
                        _hoveredTargetState = HoveredTargetState.GrassCapableAsset;
                    }
                    else
                    {
                        _hoveredTargetState = HoveredTargetState.FilteredOutAsset;
                    }

                    return;
                }
            }

            _hoveredTargetState = HoveredTargetState.UnsupportedTarget;
        }
    }

    private void PaintHoveredTarget()
    {
        if (_hoveredTarget == null)
        {
            SetStrokeStatus(new Vector4(0.95f, 0.45f, 0.45f, 1.0f), "No grass-capable hover target is currently resolved for this stroke.");
            return;
        }

        if (_strokeEntities.Contains(_hoveredTarget.Entity))
        {
            return;
        }

        if (!TryValidatePaintConfiguration(_hoveredTarget.ReadSlots(), out var validationMessage))
        {
            SetStrokeStatus(new Vector4(0.95f, 0.45f, 0.45f, 1.0f), validationMessage);
            return;
        }

        var action = _hoveredTarget.CreateApplyAction(GetOperationSlots(_hoveredTarget.ReadSlots()));
        if (action == null)
        {
            _strokeEntities.Add(_hoveredTarget.Entity);
            SetStrokeStatus(new Vector4(0.95f, 0.8f, 0.35f, 1.0f), "Stroke made no change. The target already matches the requested grass slot values.");
            return;
        }

        action.Execute();
        _strokeEntities.Add(_hoveredTarget.Entity);
        _strokeActions.Add(action);
        SetStrokeStatus(new Vector4(0.45f, 0.9f, 0.55f, 1.0f), $"Prepared grass change for {_hoveredTarget.Entity.Name}.");
    }

    private void ApplyToSelection()
    {
        var actions = new List<ViewportAction>();
        var touchedEntities = 0;

        foreach (var selectable in View.ViewportSelection.GetSelection())
        {
            if (selectable is not Entity entity)
            {
                continue;
            }

            if (!GrassPaintAdapter.TryCreate(entity, out var target) || !IsAllowedTarget(entity))
            {
                continue;
            }

            var action = target.CreateApplyAction(GetOperationSlots(target.ReadSlots()));
            if (action == null)
            {
                continue;
            }

            actions.Add(action);
            touchedEntities++;
        }

        if (actions.Count == 0)
        {
            return;
        }

        View.ViewportActionManager.ExecuteAction(new GrassPaintStrokeAction(actions, touchedEntities, false));
    }

    private void LoadFromSelection()
    {
        foreach (var selectable in View.ViewportSelection.GetSelection())
        {
            if (selectable is not Entity entity)
            {
                continue;
            }

            if (!GrassPaintAdapter.TryCreate(entity, out var target) || !IsAllowedTarget(entity))
            {
                continue;
            }

            LoadPalette(target.ReadSlots());
            return;
        }
    }

    private void LoadPalette(int[] slots)
    {
        for (var index = 0; index < _paletteSlots.Length; index++)
        {
            _paletteSlots[index] = slots[index];
        }

        _paintGrassParamId = _paletteSlots[_selectedSlot];
    }

    private void SetPaintGrassParam(int grassParamId)
    {
        _paintGrassParamId = Math.Max(0, grassParamId);
        _paletteSlots[_selectedSlot] = _paintGrassParamId;
    }

    private void DrawGrassParamPicker()
    {
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo("Grass Type", GetGrassParamDisplay(_paintGrassParamId)))
        {
            return;
        }

        ImGui.InputTextWithHint("##grassPainterParamSearch", "Search row id, name, or model...", ref _grassParamSearch, 128);
        ImGui.Separator();

        if (ImGui.Selectable("0 (empty)", _paintGrassParamId == 0))
        {
            SetPaintGrassParam(0);
        }

        if (ImGui.BeginChild("##grassPainterParamList", new Vector2(0.0f, 260.0f)))
        {
            foreach (var option in GetGrassParamOptions())
            {
                if (!MatchesGrassParamSearch(option))
                {
                    continue;
                }

                var isSelected = option.ParamId == _paintGrassParamId;

                if (!option.HasTextures)
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.6f, 0.6f, 0.6f, 1.0f));

                if (ImGui.Selectable(option.DisplayLabel, isSelected))
                {
                    SetPaintGrassParam(option.ParamId);
                }

                if (!option.HasTextures)
                    ImGui.PopStyleColor();

                if (isSelected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndChild();
        }

        ImGui.EndCombo();
    }

    private List<GrassParamOption> GetGrassParamOptions()
    {
        var grassParam = Project.Handler?.ParamData?.PrimaryBank?.GetParamFromName("GrassTypeParam");
        if (grassParam == null)
            return [];

        var rowCount = grassParam.Rows.Count;
        if (_cachedGrassParamOptions != null && _cachedGrassParamVersion == rowCount)
            return _cachedGrassParamOptions;

        var options = new List<GrassParamOption>();
        foreach (var row in grassParam.Rows.Where(entry => entry.ID > 0).OrderBy(entry => entry.ID))
        {
            var rowName = row.Name?.Trim();
            var models = BuildGrassParamModelSummary(row);
            var displayLabel = string.IsNullOrWhiteSpace(rowName)
                ? row.ID.ToString()
                : $"{row.ID} ({rowName})";

            if (!string.IsNullOrWhiteSpace(models))
            {
                displayLabel = $"{displayLabel} - {models}";
            }

            options.Add(new GrassParamOption
            {
                ParamId = row.ID,
                DisplayLabel = displayLabel,
                SearchLabel = $"{row.ID} {rowName} {models}".ToLowerInvariant(),
                HasTextures = CheckRowHasTextures(row)
            });
        }

        _cachedGrassParamOptions = options;
        _cachedGrassParamVersion = rowCount;
        return options;
    }

    private bool MatchesGrassParamSearch(GrassParamOption option)
    {
        if (string.IsNullOrWhiteSpace(_grassParamSearch))
        {
            return true;
        }

        return option.SearchLabel.Contains(_grassParamSearch.Trim().ToLowerInvariant(), StringComparison.Ordinal);
    }

    private int[] GetOperationSlots(int[] currentSlots)
    {
        var nextSlots = currentSlots.ToArray();

        switch (_operation)
        {
            case GrassPaintOperation.PaintSlot:
                nextSlots[_selectedSlot] = _paintGrassParamId;
                break;
            case GrassPaintOperation.ApplyPalette:
                for (var index = 0; index < _paletteSlots.Length; index++)
                {
                    nextSlots[index] = _paletteSlots[index];
                }
                break;
            case GrassPaintOperation.ClearSlot:
                nextSlots[_selectedSlot] = 0;
                break;
            case GrassPaintOperation.ClearAll:
                Array.Fill(nextSlots, 0);
                break;
        }

        return nextSlots;
    }

    private bool IsAllowedTarget(Entity entity)
    {
        var isMapPiece = EntityHelper.IsPartMapPiece(entity);
        var isAsset = EntityHelper.IsPartPureAsset(entity);
        return (isMapPiece && _allowMapPieces) || (isAsset && _allowAssets);
    }

    private void CommitStroke()
    {
        _strokeInProgress = false;

        if (_strokeActions.Count == 0)
        {
            _strokeEntities.Clear();
            ClearStrokeLock();
            CommitStrokePreviewGroups();
            if (_paintEnabled)
            {
                SetStrokeStatus(new Vector4(0.95f, 0.45f, 0.45f, 1.0f), "Stroke finished without writing any grass changes.");
            }
            return;
        }

        var strokeAction = new GrassPaintStrokeAction(_strokeActions.ToList(), _strokeActions.Count, true);
        View.ViewportActionManager.ExecuteAction(strokeAction);
        CommitStrokePreviewGroups();
        SetStrokeStatus(new Vector4(0.45f, 0.9f, 0.55f, 1.0f), $"Committed grass stroke across {_strokeActions.Count} target(s).");

        _strokeActions.Clear();
        _strokeEntities.Clear();
        ClearStrokeLock();
    }

    private void CancelStroke()
    {
        if (_strokeActions.Count > 0)
        {
            for (var index = _strokeActions.Count - 1; index >= 0; index--)
            {
                _strokeActions[index].Undo();
            }
        }

        _strokeInProgress = false;
        _strokeActions.Clear();
        _strokeEntities.Clear();
        ClearStrokeLock();
        DisposeGrassPreviewGroups(_strokeTrailGrassPreviewGroups);
        _strokeTrailPositions.Clear();
        _strokeTrailScales.Clear();
        _strokeTrailNormals.Clear();
        SetMarkersVisible(_strokeTrailMarkers, false);
    }

    private void CommitStrokePreviewGroups()
    {
        if (_strokeTrailGrassPreviewGroups.Count == 0)
        {
            _strokeTrailPositions.Clear();
            _strokeTrailScales.Clear();
            _strokeTrailNormals.Clear();
            SetMarkersVisible(_strokeTrailMarkers, false);
            return;
        }

        _committedGrassPreviewGroups.AddRange(_strokeTrailGrassPreviewGroups);
        _strokeTrailGrassPreviewGroups.Clear();
        _strokeTrailPositions.Clear();
        _strokeTrailScales.Clear();
        _strokeTrailNormals.Clear();
        SetMarkersVisible(_strokeTrailMarkers, false);
    }

    private string GetGrassParamDisplay(int grassParamId)
    {
        if (grassParamId == 0)
        {
            return "0 (empty)";
        }

        if (_grassParamNames.TryGetValue(grassParamId, out var cachedName))
        {
            return cachedName;
        }

        var displayName = grassParamId.ToString();
        var paramData = Project.Handler.ParamData;

        if (paramData?.PrimaryBank?.Params != null &&
            paramData.PrimaryBank.Params.TryGetValue("GrassTypeParam", out var grassTypeParam))
        {
            var row = grassTypeParam.Rows.FirstOrDefault(entry => entry.ID == grassParamId);
            if (row != null)
            {
                var rowName = row.Name?.Trim();
                var modelSummary = BuildGrassParamModelSummary(row);

                displayName = string.IsNullOrWhiteSpace(rowName)
                    ? grassParamId.ToString()
                    : $"{grassParamId} ({rowName})";

                if (!string.IsNullOrWhiteSpace(modelSummary))
                {
                    displayName = $"{displayName} - {modelSummary}";
                }
            }
        }

        _grassParamNames[grassParamId] = displayName;
        return displayName;
    }

    private string BuildGrassParamModelSummary(Param.Row row)
    {
        var modelNames = new[]
        {
            GetRowString(row, "model0Name"),
            GetRowString(row, "simpleModelName"),
            GetRowString(row, "model1Name")
        }
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(3)
        .ToArray();

        if (modelNames.Length > 0)
        {
            var parts = modelNames.Select(name =>
            {
                var alias = AliasHelper.GetAssetAlias(Project, name.ToLowerInvariant());
                return string.IsNullOrEmpty(alias) ? name : $"{name} ({alias})";
            });
            return string.Join(", ", parts);
        }

        var textureName = FirstNonEmpty(GetRowString(row, "flatTextureName"), GetRowString(row, "billboardTextureName"));
        return string.IsNullOrWhiteSpace(textureName) ? "" : textureName;
    }

    private bool CheckModelHasTextures(string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
            return false;

        ResourceDescriptor texDesc;
        if (modelName.StartsWith("AEG", StringComparison.OrdinalIgnoreCase))
            texDesc = TextureLocator.GetAssetTextureVirtualPath(Project, modelName);
        else if (modelName.StartsWith("o", StringComparison.OrdinalIgnoreCase))
            texDesc = TextureLocator.GetObjectTextureVirtualPath(Project, modelName);
        else
            return false;

        var virtPath = texDesc.AssetVirtualPath ?? texDesc.AssetArchiveVirtualPath;
        if (string.IsNullOrWhiteSpace(virtPath))
            return false;

        var relativePath = PathBuilder.GetRelativePath(Project, virtPath);
        if (string.IsNullOrWhiteSpace(relativePath))
            return false;

        return Project.VFS.FS.FileExists(relativePath);
    }

    private bool CheckRowHasTextures(Param.Row row)
    {
        var modelNames = new[]
        {
            GetRowString(row, "model0Name"),
            GetRowString(row, "simpleModelName"),
            GetRowString(row, "model1Name")
        };

        foreach (var name in modelNames)
        {
            if (!string.IsNullOrWhiteSpace(name) && CheckModelHasTextures(name))
                return true;
        }

        return false;
    }

    private bool TryValidatePaintConfiguration(int[] currentSlots, out string message)
    {
        switch (_operation)
        {
            case GrassPaintOperation.PaintSlot:
                if (_paintGrassParamId == 0)
                {
                    message = "Paint Grass Param is 0 (empty). Choose a non-zero GrassTypeParam row before painting.";
                    return false;
                }

                break;
            case GrassPaintOperation.ApplyPalette:
                if (_paletteSlots.All(slot => slot == 0))
                {
                    message = "Palette apply is all zeroes, so it would only clear grass instead of seeding it.";
                    return false;
                }

                break;
        }

        var nextSlots = GetOperationSlots(currentSlots);
        if (currentSlots.SequenceEqual(nextSlots))
        {
            message = "The target already matches the current stroke configuration.";
            return false;
        }

        message = null;
        return true;
    }

    private void TrySeedDefaultPaintValue()
    {
        if (_paintGrassParamId != 0)
        {
            return;
        }

        var grassParam = Project.Handler?.ParamData?.PrimaryBank?.GetParamFromName("GrassTypeParam");
        if (grassParam == null)
        {
            return;
        }

        var firstUsableRow = grassParam.Rows.FirstOrDefault(row => row.ID > 0);
        if (firstUsableRow == null)
        {
            return;
        }

        SetPaintGrassParam(firstUsableRow.ID);
        SetStrokeStatus(new Vector4(0.45f, 0.9f, 0.55f, 1.0f), $"Seeded paint value from GrassTypeParam: {GetGrassParamDisplay(_paintGrassParamId)}.");
    }

    private void SetStrokeStatus(Vector4 color, string message)
    {
        _strokeStatusColor = color;
        _strokeStatusMessage = message;
    }

    private void LockStrokeTarget()
    {
        _lockedStrokeTarget = _hoveredTarget;
        _lockedStrokeEntity = _hoveredEntity;
        _lockedStrokeTargetState = _hoveredTargetState;
    }

    private void RestoreLockedStrokeHover()
    {
        _hoveredTarget = _lockedStrokeTarget;
        _hoveredEntity = _lockedStrokeEntity;
        _hoveredTargetState = _lockedStrokeTargetState;
    }

    private void ClearStrokeLock()
    {
        _lockedStrokeTarget = null;
        _lockedStrokeEntity = null;
        _lockedStrokeTargetState = HoveredTargetState.None;
    }

    private void DrawHoveredStateLabel()
    {
        var (color, label) = GetHoveredStatePresentation();

        if (label == null)
        {
            return;
        }

        ImGui.TextColored(color, label);
    }

    private void UpdateBrushPreview(VulkanViewport viewport)
    {
        var previewEntity = _strokeInProgress && _lockedStrokeEntity != null ? _lockedStrokeEntity : _hoveredEntity;

        if (previewEntity == null)
        {
            HideBrushPreview();
            return;
        }

        EnsureBrushPreviewProxy(viewport.RenderScene);

        if (_brushPreviewProxy == null)
        {
            return;
        }

        _brushPreviewProxy.SetSelectable(previewEntity);

        if (!TryGetBrushPreviewAnchor(viewport, previewEntity, out var previewPosition, out var previewRadius, out var previewNormal))
        {
            HideBrushPreview();
            return;
        }

        var previewColor = GetHoveredStateBrushColor();
        var centerScale = Math.Clamp(previewRadius * 0.12f, 0.08f, 0.24f);
        _brushPreviewProxy.BaseColor = ColorHelper.GetTransparencyColor(previewColor, _strokeInProgress ? 65.0f : 52.0f);
        _brushPreviewProxy.HighlightedColor = ColorHelper.GetTransparencyColor(previewColor, _strokeInProgress ? 75.0f : 62.0f);
        _brushPreviewProxy.World = Matrix4x4.CreateScale(centerScale) * Matrix4x4.CreateTranslation(previewPosition);
        _brushPreviewProxy.Visible = true;

        UpdateBrushSurfaceMarkers(previewEntity, previewPosition, previewRadius, previewNormal, previewColor);

        if (_strokeInProgress)
        {
            AddStrokeTrailStamp(previewPosition, previewRadius, previewNormal);
            UpdateStrokeTrailMarkers(previewEntity, previewColor);
        }
    }

    private bool TryGetBrushPreviewAnchor(VulkanViewport viewport, Entity previewEntity, out Vector3 previewPosition, out float previewRadius, out Vector3 previewNormal)
    {
        previewPosition = Vector3.Zero;
        previewRadius = 1.0f;
        previewNormal = Vector3.UnitY;

        if (previewEntity == null || !viewport.MouseInViewport())
        {
            return false;
        }

        var mousePosition = InputManager.MousePosition;
        var ray = viewport.GetRay(mousePosition.X - viewport.X, mousePosition.Y - viewport.Y);
        var bounds = previewEntity.GetBounds();

        // Use bounds for radius computation.
        var extents = bounds.Max - bounds.Min;
        var horizontalRadius = MathF.Max(extents.X, extents.Z) * 0.06f;
        previewRadius = Math.Clamp(horizontalRadius, 0.35f, 4.0f);

        // Try CPU ray-mesh intersection for accurate surface placement.
        if (TryRaycastEntityMesh(ray, previewEntity, out var meshHitDist, out var meshHitNormal))
        {
            previewPosition = ray.Origin + ray.Direction * meshHitDist;
            previewNormal = meshHitNormal;
            previewPosition += previewNormal * MathF.Max(previewRadius * 0.08f, 0.03f);
            return true;
        }

        // Fall back to bounding box intersection.
        if (!TryIntersectBounds(ray, bounds, out var hitDistance, out previewNormal))
        {
            return false;
        }

        previewPosition = ray.Origin + ray.Direction * hitDistance;
        previewPosition += previewNormal * MathF.Max(previewRadius * 0.08f, 0.03f);
        return true;
    }

    private bool TryRaycastEntityMesh(Ray ray, Entity entity, out float hitDistance, out Vector3 hitNormal)
    {
        hitDistance = 0.0f;
        hitNormal = Vector3.UnitY;

        var meshData = GetOrLoadRaycastMesh(entity);
        if (meshData == null)
        {
            return false;
        }

        // Transform ray into entity local space.
        var worldMatrix = entity.GetWorldMatrix();
        if (!Matrix4x4.Invert(worldMatrix, out var invWorld))
        {
            return false;
        }

        var localOrigin = Vector3.Transform(ray.Origin, invWorld);
        var localDir = Vector3.Normalize(Vector3.TransformNormal(ray.Direction, invWorld));
        var localRay = new Ray(localOrigin, localDir);

        if (!ViewportUtils.RayMeshIntersection(localRay, meshData.Vertices, meshData.Indices, ViewportUtils.RayCastCull.CullNone, out var localDist))
        {
            return false;
        }

        // Compute hit point in world space.
        var localHitPoint = localOrigin + localDir * localDist;
        var worldHitPoint = Vector3.Transform(localHitPoint, worldMatrix);
        hitDistance = Vector3.Distance(ray.Origin, worldHitPoint);

        // Compute triangle normal at the hit point.
        for (var i = 0; i < meshData.Indices.Length; i += 3)
        {
            ref var v0 = ref meshData.Vertices[meshData.Indices[i]];
            ref var v1 = ref meshData.Vertices[meshData.Indices[i + 1]];
            ref var v2 = ref meshData.Vertices[meshData.Indices[i + 2]];

            if (localRay.Intersects(ref v0, ref v1, ref v2, out var triDist) && MathF.Abs(triDist - localDist) < 0.001f)
            {
                var localNormal = Vector3.Normalize(Vector3.Cross(v1 - v0, v2 - v0));
                hitNormal = Vector3.Normalize(Vector3.TransformNormal(localNormal, worldMatrix));
                break;
            }
        }

        return true;
    }

    private RaycastMeshData GetOrLoadRaycastMesh(Entity entity)
    {
        var modelName = entity.CurrentModelName;
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return null;
        }

        // Build a cache key from model name + map ID for map pieces.
        var cacheKey = modelName;
        string mapId = null;
        if (entity is MsbEntity msbEntity)
        {
            mapId = msbEntity.MapID;
            if (!string.IsNullOrWhiteSpace(mapId) && modelName.StartsWith("m", StringComparison.OrdinalIgnoreCase))
            {
                cacheKey = $"{mapId}/{modelName}";
            }
        }

        if (_raycastMeshCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        // Resolve the model to a ResourceDescriptor.
        if (!TryResolveEntityModelAsset(entity, modelName, mapId, out var asset))
        {
            _raycastMeshCache[cacheKey] = null;
            return null;
        }

        // Get the relative path for VFS read.
        var relativePath = PathBuilder.GetRelativePath(Project, asset.AssetVirtualPath);
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            _raycastMeshCache[cacheKey] = null;
            return null;
        }

        // Read and parse the FLVER.
        try
        {
            var flver = TryReadFlverFromVFS(relativePath);
            if (flver == null || flver.Meshes == null || flver.Meshes.Count == 0)
            {
                _raycastMeshCache[cacheKey] = null;
                return null;
            }

            // Extract vertices and triangulated indices from all meshes.
            var allVertices = new List<Vector3>();
            var allIndices = new List<int>();

            foreach (var mesh in flver.Meshes)
            {
                if (mesh.Vertices == null || mesh.Vertices.Count == 0)
                {
                    continue;
                }

                var vertexOffset = allVertices.Count;
                foreach (var vert in mesh.Vertices)
                {
                    allVertices.Add(vert.Position);
                }

                foreach (var faceSet in mesh.FaceSets)
                {
                    if (faceSet.Flags != FLVER2.FaceSet.FSFlags.None &&
                        faceSet.Flags != FLVER2.FaceSet.FSFlags.EdgeCompressed)
                    {
                        continue;
                    }

                    var triangulated = faceSet.Triangulate(mesh.Vertices.Count < ushort.MaxValue);
                    foreach (var idx in triangulated)
                    {
                        if (idx < 0 || idx >= mesh.Vertices.Count)
                        {
                            continue;
                        }

                        allIndices.Add(idx + vertexOffset);
                    }
                }
            }

            if (allVertices.Count == 0 || allIndices.Count < 3)
            {
                _raycastMeshCache[cacheKey] = null;
                return null;
            }

            var meshData = new RaycastMeshData
            {
                Vertices = allVertices.ToArray(),
                Indices = allIndices.ToArray()
            };
            _raycastMeshCache[cacheKey] = meshData;
            return meshData;
        }
        catch
        {
            _raycastMeshCache[cacheKey] = null;
            return null;
        }
    }

    private FLVER2 TryReadFlverFromVFS(string relativePath)
    {
        var fileData = Project.VFS.FS.ReadFile(relativePath);
        if (fileData == null || fileData.Value.Length <= 1)
        {
            return null;
        }

        // If the file is a loose FLVER, parse directly.
        if (relativePath.EndsWith(".flver", StringComparison.OrdinalIgnoreCase) ||
            relativePath.EndsWith(".flver.dcx", StringComparison.OrdinalIgnoreCase) ||
            relativePath.EndsWith(".flv", StringComparison.OrdinalIgnoreCase) ||
            relativePath.EndsWith(".flv.dcx", StringComparison.OrdinalIgnoreCase))
        {
            return FLVER2.Read(fileData.Value);
        }

        // Otherwise it's a binder archive — extract the FLVER from inside it.
        var binder = BND4.Read(fileData.Value);
        foreach (var file in binder.Files)
        {
            if (file.Name != null &&
                (file.Name.EndsWith(".flver", StringComparison.OrdinalIgnoreCase) ||
                 file.Name.EndsWith(".flv", StringComparison.OrdinalIgnoreCase)))
            {
                return FLVER2.Read(file.Bytes);
            }
        }

        return null;
    }

    private bool TryResolveEntityModelAsset(Entity entity, string modelName, string mapId, out ResourceDescriptor asset)
    {
        asset = default;

        if (modelName.StartsWith("m", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(mapId))
        {
            var amapid = PathBuilder.GetAssetMapID(Project, mapId);
            var mapAssetName = ModelLocator.MapModelNameToAssetName(Project, amapid, modelName);
            asset = ModelLocator.GetMapModel(Project, amapid, mapAssetName, mapAssetName);
            return !string.IsNullOrWhiteSpace(asset.AssetVirtualPath) && asset.AssetVirtualPath != "null";
        }

        if (modelName.StartsWith("o", StringComparison.OrdinalIgnoreCase) || modelName.StartsWith("AEG", StringComparison.OrdinalIgnoreCase))
        {
            asset = ModelLocator.GetObjModel(Project, modelName, modelName);
            return !string.IsNullOrWhiteSpace(asset.AssetVirtualPath) && asset.AssetVirtualPath != "null";
        }

        if (modelName.StartsWith("c", StringComparison.OrdinalIgnoreCase))
        {
            asset = ModelLocator.GetChrModel(Project, modelName, modelName);
            return !string.IsNullOrWhiteSpace(asset.AssetVirtualPath) && asset.AssetVirtualPath != "null";
        }

        return false;
    }

    private bool TryIntersectBounds(Ray ray, BoundingBox bounds, out float distance, out Vector3 normal)
    {
        distance = 0.0f;
        normal = Vector3.UnitY;

        var tMin = float.NegativeInfinity;
        var tMax = float.PositiveInfinity;
        var enterNormal = Vector3.Zero;
        var exitNormal = Vector3.Zero;

        if (!TryIntersectAxis(ray.Origin.X, ray.Direction.X, bounds.Min.X, bounds.Max.X, -Vector3.UnitX, Vector3.UnitX, ref tMin, ref tMax, ref enterNormal, ref exitNormal) ||
            !TryIntersectAxis(ray.Origin.Y, ray.Direction.Y, bounds.Min.Y, bounds.Max.Y, -Vector3.UnitY, Vector3.UnitY, ref tMin, ref tMax, ref enterNormal, ref exitNormal) ||
            !TryIntersectAxis(ray.Origin.Z, ray.Direction.Z, bounds.Min.Z, bounds.Max.Z, -Vector3.UnitZ, Vector3.UnitZ, ref tMin, ref tMax, ref enterNormal, ref exitNormal))
        {
            return false;
        }

        if (tMax < 0.0f)
        {
            return false;
        }

        if (tMin >= 0.0f)
        {
            distance = tMin;
            normal = enterNormal == Vector3.Zero ? Vector3.UnitY : enterNormal;
        }
        else
        {
            distance = tMax;
            normal = exitNormal == Vector3.Zero ? Vector3.UnitY : exitNormal;
        }

        return true;
    }

    private bool TryIntersectAxis(float origin, float direction, float min, float max, Vector3 minNormal, Vector3 maxNormal, ref float tMin, ref float tMax, ref Vector3 enterNormal, ref Vector3 exitNormal)
    {
        if (MathF.Abs(direction) < 0.0001f)
        {
            return origin >= min && origin <= max;
        }

        var inverseDirection = 1.0f / direction;
        var t1 = (min - origin) * inverseDirection;
        var t2 = (max - origin) * inverseDirection;
        var n1 = minNormal;
        var n2 = maxNormal;

        if (t1 > t2)
        {
            (t1, t2) = (t2, t1);
            (n1, n2) = (n2, n1);
        }

        if (t1 > tMin)
        {
            tMin = t1;
            enterNormal = n1;
        }

        if (t2 < tMax)
        {
            tMax = t2;
            exitNormal = n2;
        }

        return tMin <= tMax;
    }

    private void EnsureBrushPreviewProxy(RenderScene scene)
    {
        if (_brushPreviewScene == scene && _brushPreviewProxy != null)
        {
            return;
        }

        _brushPreviewProxy?.Dispose();
        _brushPreviewScene = scene;

        if (scene == null)
        {
            _brushPreviewProxy = null;
            DisposeMarkers(_brushPreviewMarkers);
            DisposeMarkers(_strokeTrailMarkers);
            DisposeGrassPreviewGroups(_strokeTrailGrassPreviewGroups);
            DisposeGrassPreviewGroups(_committedGrassPreviewGroups);
            _strokeTrailPositions.Clear();
            _strokeTrailScales.Clear();
            _strokeTrailNormals.Clear();
            return;
        }

        _brushPreviewProxy = RenderableHelper.GetAutoInvadeSphereProxy(scene);
        _brushPreviewProxy.RenderOverlay = true;
        _brushPreviewProxy.Visible = false;
        _brushPreviewProxy.DrawGroups = new DrawGroup();

        DisposeMarkers(_brushPreviewMarkers);
        for (var index = 0; index < BrushPreviewMarkerCount; index++)
        {
            var marker = CreateBrushMarkerProxy(scene);
            _brushPreviewMarkers.Add(marker);
        }

        DisposeMarkers(_strokeTrailMarkers);
        for (var index = 0; index < StrokeTrailCapacity; index++)
        {
            var marker = CreateBrushMarkerProxy(scene);
            _strokeTrailMarkers.Add(marker);
        }

        _strokeTrailPositions.Clear();
        _strokeTrailScales.Clear();
        _strokeTrailNormals.Clear();
        DisposeGrassPreviewGroups(_strokeTrailGrassPreviewGroups);
        DisposeGrassPreviewGroups(_committedGrassPreviewGroups);
    }

    private void HideBrushPreview()
    {
        if (_brushPreviewProxy != null)
        {
            _brushPreviewProxy.Visible = false;
        }

        SetMarkersVisible(_brushPreviewMarkers, false);
    }

    private DebugPrimitiveRenderableProxy CreateBrushMarkerProxy(RenderScene scene)
    {
        var marker = RenderableHelper.GetPlacementOrbProxy(scene);
        marker.RenderOverlay = true;
        marker.Visible = false;
        marker.DrawGroups = new DrawGroup();
        return marker;
    }

    private void UpdateBrushSurfaceMarkers(Entity previewEntity, Vector3 previewPosition, float previewRadius, Vector3 previewNormal, Color previewColor)
    {
        if (_brushPreviewMarkers.Count != BrushPreviewMarkerCount)
        {
            return;
        }

        var tangent = GetPreviewTangent(previewNormal);
        var bitangent = Vector3.Normalize(Vector3.Cross(previewNormal, tangent));
        var lift = previewNormal * MathF.Max(previewRadius * 0.04f, 0.015f);
        var centerScale = Math.Clamp(previewRadius * 0.11f, 0.05f, 0.16f);
        var innerScale = Math.Clamp(previewRadius * 0.09f, 0.045f, 0.12f);
        var outerScale = Math.Clamp(previewRadius * 0.075f, 0.04f, 0.1f);

        UpdateMarker(_brushPreviewMarkers[0], previewEntity, previewPosition, centerScale, previewColor, 82.0f);

        for (var index = 0; index < 4; index++)
        {
            var angle = MathF.Tau * index / 4.0f;
            var offset = (MathF.Cos(angle) * tangent + MathF.Sin(angle) * bitangent) * (previewRadius * 0.42f);
            UpdateMarker(_brushPreviewMarkers[index + 1], previewEntity, previewPosition + offset + lift, innerScale, previewColor, 56.0f);
        }

        for (var index = 0; index < 8; index++)
        {
            var angle = MathF.Tau * index / 8.0f;
            var offset = (MathF.Cos(angle) * tangent + MathF.Sin(angle) * bitangent) * (previewRadius * 0.88f);
            UpdateMarker(_brushPreviewMarkers[index + 5], previewEntity, previewPosition + offset + lift, outerScale, previewColor, 42.0f);
        }
    }

    private void AddStrokeTrailStamp(Vector3 previewPosition, float previewRadius, Vector3 previewNormal)
    {
        if (_strokeTrailPositions.Count > 0)
        {
            var minimumSpacing = MathF.Max(previewRadius * 0.55f, 0.12f);
            if (Vector3.DistanceSquared(_strokeTrailPositions[^1], previewPosition) < minimumSpacing * minimumSpacing)
            {
                return;
            }
        }

        if (_strokeTrailPositions.Count == StrokeTrailCapacity)
        {
            _strokeTrailPositions.RemoveAt(0);
            _strokeTrailScales.RemoveAt(0);
            _strokeTrailNormals.RemoveAt(0);

            if (_strokeTrailGrassPreviewGroups.Count > 0)
            {
                DisposeGrassPreviewGroup(_strokeTrailGrassPreviewGroups[0]);
                _strokeTrailGrassPreviewGroups.RemoveAt(0);
            }
        }

        _strokeTrailPositions.Add(previewPosition);
        _strokeTrailScales.Add(Math.Clamp(previewRadius * 0.1f, 0.05f, 0.14f));
        _strokeTrailNormals.Add(previewNormal);

        if (_lockedStrokeEntity != null && TryGetActiveGrassPreviewSettings(out var previewSettings, out _))
        {
            var previewGroup = CreateGrassPreviewGroup(_lockedStrokeEntity, previewPosition, previewNormal, previewRadius, previewSettings, _strokeTrailPositions.Count - 1);
            _strokeTrailGrassPreviewGroups.Add(previewGroup);
        }
    }

    private void UpdateStrokeTrailMarkers(Entity previewEntity, Color previewColor)
    {
        for (var index = 0; index < _strokeTrailMarkers.Count; index++)
        {
            if (index >= _strokeTrailPositions.Count)
            {
                _strokeTrailMarkers[index].Visible = false;
                continue;
            }

            UpdateMarker(_strokeTrailMarkers[index], previewEntity, _strokeTrailPositions[index], _strokeTrailScales[index], previewColor, 68.0f);
        }
    }

    private void UpdateMarker(DebugPrimitiveRenderableProxy marker, Entity previewEntity, Vector3 position, float scale, Color previewColor, float alpha)
    {
        marker.SetSelectable(previewEntity);
        marker.BaseColor = ColorHelper.GetTransparencyColor(previewColor, alpha);
        marker.HighlightedColor = ColorHelper.GetTransparencyColor(previewColor, MathF.Min(alpha + 10.0f, 100.0f));
        marker.World = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateTranslation(position);
        marker.Visible = true;
    }

    private Vector3 GetPreviewTangent(Vector3 normal)
    {
        var reference = MathF.Abs(Vector3.Dot(normal, Vector3.UnitY)) > 0.9f ? Vector3.UnitX : Vector3.UnitY;
        return Vector3.Normalize(Vector3.Cross(reference, normal));
    }

    private void SetMarkersVisible(List<DebugPrimitiveRenderableProxy> markers, bool visible)
    {
        foreach (var marker in markers)
        {
            marker.Visible = visible;
        }
    }

    private void DisposeMarkers(List<DebugPrimitiveRenderableProxy> markers)
    {
        foreach (var marker in markers)
        {
            marker.Dispose();
        }

        markers.Clear();
    }

    private string GetActivePreviewSummary()
    {
        return TryGetActiveGrassPreviewSettings(out var previewSettings, out var fallbackReason)
            ? previewSettings.Summary
            : fallbackReason;
    }

    private bool TryGetActiveGrassPreviewSettings(out GrassPreviewSettings previewSettings, out string fallbackReason)
    {
        previewSettings = null;
        fallbackReason = null;

        var activeParamId = GetPreviewGrassParamId();
        if (activeParamId <= 0)
        {
            fallbackReason = "No non-zero grass param is selected for preview.";
            return false;
        }

        var grassParam = Project.Handler?.ParamData?.PrimaryBank?.GetParamFromName("GrassTypeParam");
        var row = grassParam?.Rows.FirstOrDefault(entry => entry.ID == activeParamId);

        if (row == null)
        {
            fallbackReason = $"GrassTypeParam row {activeParamId} is not loaded.";
            return false;
        }

        var clusterType = GetRowInt(row, "lod0ClusterType", 0);
        var model0Name = GetRowString(row, "model0Name");
        var model1Name = GetRowString(row, "model1Name");
        var simpleModelName = GetRowString(row, "simpleModelName");

        string selectedModelName;
        string sourceLabel;

        switch (clusterType)
        {
            case 1:
                selectedModelName = FirstNonEmpty(model0Name, model1Name);
                sourceLabel = "LOD0 model";
                break;
            case 4:
                selectedModelName = FirstNonEmpty(simpleModelName, model0Name, model1Name);
                sourceLabel = "simple model";
                break;
            case 2:
                fallbackReason = $"GrassTypeParam {activeParamId} uses billboard grass, so Smithbox falls back to surface stamps.";
                return false;
            case 3:
                fallbackReason = $"GrassTypeParam {activeParamId} uses flat-tuft grass, so Smithbox falls back to surface stamps.";
                return false;
            default:
                selectedModelName = FirstNonEmpty(model0Name, simpleModelName, model1Name);
                sourceLabel = "grass model";
                break;
        }

        if (string.IsNullOrWhiteSpace(selectedModelName))
        {
            fallbackReason = $"GrassTypeParam {activeParamId} has no usable model name for preview.";
            return false;
        }

        selectedModelName = selectedModelName.Trim();
        if (!CanPreviewModel(selectedModelName))
        {
            fallbackReason = $"GrassTypeParam {activeParamId} points to unsupported preview model '{selectedModelName}'.";
            return false;
        }

        previewSettings = new GrassPreviewSettings
        {
            ModelName = selectedModelName,
            SourceParamId = activeParamId,
            ClusterType = clusterType,
            BaseDensity = GetRowFloat(row, "baseDensity", 1.0f),
            Spacing = MathF.Max(GetRowFloat(row, "spacing", 0.3f), 0.05f),
            WidthScaleMin = MathF.Max(GetRowFloat(row, "scaleBaseMin", 100.0f) / 100.0f, 0.1f),
            WidthScaleMax = MathF.Max(GetRowFloat(row, "scaleBaseMax", 100.0f) / 100.0f, 0.1f),
            HeightScaleMin = MathF.Max(GetRowFloat(row, "scaleHeightMin", 100.0f) / 100.0f, 0.1f),
            HeightScaleMax = MathF.Max(GetRowFloat(row, "scaleHeightMax", 100.0f) / 100.0f, 0.1f),
            OrientationAngle = GetRowFloat(row, "orientationAngle", -1.0f),
            OrientationRange = GetRowFloat(row, "orientationRange", -1.0f),
            Summary = $"{sourceLabel}: {selectedModelName} from GrassTypeParam {activeParamId}"
        };

        return true;
    }

    private int GetPreviewGrassParamId()
    {
        if (_operation == GrassPaintOperation.PaintSlot)
        {
            return _paintGrassParamId;
        }

        if (_operation == GrassPaintOperation.ApplyPalette)
        {
            return _paletteSlots.FirstOrDefault(slot => slot > 0);
        }

        return 0;
    }

    private int GetRowInt(Param.Row row, string fieldName, int fallbackValue)
    {
        var cell = row[fieldName];
        if (cell == null)
        {
            return fallbackValue;
        }

        try
        {
            return Convert.ToInt32(UnwrapParamValue(cell.Value));
        }
        catch
        {
            return fallbackValue;
        }
    }

    private float GetRowFloat(Param.Row row, string fieldName, float fallbackValue)
    {
        var cell = row[fieldName];
        if (cell == null)
        {
            return fallbackValue;
        }

        try
        {
            return Convert.ToSingle(UnwrapParamValue(cell.Value));
        }
        catch
        {
            return fallbackValue;
        }
    }

    private string GetRowString(Param.Row row, string fieldName)
    {
        var cell = row[fieldName];
        return UnwrapParamValue(cell?.Value)?.ToString()?.Trim('\0', ' ');
    }

    private object UnwrapParamValue(object value)
    {
        var currentValue = value;
        while (currentValue != null)
        {
            var valueProperty = currentValue.GetType().GetProperty("Value");
            if (valueProperty == null)
            {
                break;
            }

            var nextValue = valueProperty.GetValue(currentValue);
            if (ReferenceEquals(nextValue, currentValue))
            {
                break;
            }

            currentValue = nextValue;
        }

        return currentValue;
    }

    private string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private bool CanPreviewModel(string modelName)
    {
        return modelName.StartsWith("o", StringComparison.OrdinalIgnoreCase) ||
               modelName.StartsWith("AEG", StringComparison.OrdinalIgnoreCase) ||
               modelName.StartsWith("m", StringComparison.OrdinalIgnoreCase) ||
               modelName.StartsWith("c", StringComparison.OrdinalIgnoreCase);
    }

    private List<MeshRenderableProxy> CreateGrassPreviewGroup(Entity previewEntity, Vector3 centerPosition, Vector3 normal, float previewRadius, GrassPreviewSettings previewSettings, int stampIndex)
    {
        var group = new List<MeshRenderableProxy>();

        if (_brushPreviewScene == null)
        {
            return group;
        }

        if (!TryResolvePreviewAsset(previewEntity, previewSettings.ModelName, out var asset))
        {
            return group;
        }

        EnsurePreviewAssetLoaded(asset, previewSettings.ModelName);

        var instanceCount = previewSettings.GetPreviewInstanceCount();
        for (var instanceIndex = 0; instanceIndex < instanceCount; instanceIndex++)
        {
            var proxy = MeshRenderableProxy.MeshRenderableFromFlverResource(_brushPreviewScene, asset.AssetVirtualPath, ModelMarkerType.None, null);
            proxy.SetSelectable(previewEntity);
            proxy.DrawFilter = RenderFilter.Object;
            proxy.DrawGroups = new DrawGroup();
            proxy.World = BuildGrassPreviewTransform(centerPosition, normal, previewRadius, previewSettings, stampIndex, instanceIndex);
            group.Add(proxy);
        }

        return group;
    }

    private bool TryResolvePreviewAsset(Entity previewEntity, string modelName, out ResourceDescriptor asset)
    {
        asset = default;

        if (modelName.StartsWith("o", StringComparison.OrdinalIgnoreCase) || modelName.StartsWith("AEG", StringComparison.OrdinalIgnoreCase))
        {
            asset = ModelLocator.GetObjModel(Project, modelName, modelName);
            return !string.IsNullOrWhiteSpace(asset.AssetVirtualPath) && asset.AssetVirtualPath != "null";
        }

        if (modelName.StartsWith("c", StringComparison.OrdinalIgnoreCase))
        {
            asset = ModelLocator.GetChrModel(Project, modelName, modelName);
            return !string.IsNullOrWhiteSpace(asset.AssetVirtualPath) && asset.AssetVirtualPath != "null";
        }

        if (modelName.StartsWith("m", StringComparison.OrdinalIgnoreCase) && previewEntity is MsbEntity msbEntity && !string.IsNullOrWhiteSpace(msbEntity.MapID))
        {
            var mapAssetName = ModelLocator.MapModelNameToAssetName(Project, msbEntity.MapID, modelName);
            asset = ModelLocator.GetMapModel(Project, msbEntity.MapID, mapAssetName, mapAssetName);
            return !string.IsNullOrWhiteSpace(asset.AssetVirtualPath) && asset.AssetVirtualPath != "null";
        }

        return false;
    }

    private void EnsurePreviewAssetLoaded(ResourceDescriptor asset, string modelName = null)
    {
        if (string.IsNullOrWhiteSpace(asset.AssetVirtualPath) || asset.AssetVirtualPath == "null")
        {
            return;
        }

        if (!ResourceManager.IsResourceLoaded(asset.AssetVirtualPath, AccessLevel.AccessGPUOptimizedOnly))
        {
            var job = ResourceManager.CreateNewJob("Loading grass preview mesh");
            if (!string.IsNullOrWhiteSpace(asset.AssetArchiveVirtualPath) && asset.AssetArchiveVirtualPath != "null")
            {
                job.AddLoadArchiveTask(asset.AssetArchiveVirtualPath, AccessLevel.AccessGPUOptimizedOnly, false, ResourceType.Flver);
            }
            else
            {
                job.AddLoadFileTask(asset.AssetVirtualPath, AccessLevel.AccessGPUOptimizedOnly);
            }

            Task loadTask = job.Complete();
            if (View.Universe.HasProcessedMapLoad)
            {
                loadTask.Wait();
            }
        }

        if (!string.IsNullOrWhiteSpace(modelName))
        {
            EnsurePreviewTexturesLoaded(modelName);
        }
    }

    private void EnsurePreviewTexturesLoaded(string modelName)
    {
        ResourceDescriptor texDesc;
        if (modelName.StartsWith("AEG", StringComparison.OrdinalIgnoreCase))
            texDesc = TextureLocator.GetAssetTextureVirtualPath(Project, modelName);
        else if (modelName.StartsWith("o", StringComparison.OrdinalIgnoreCase))
            texDesc = TextureLocator.GetObjectTextureVirtualPath(Project, modelName);
        else
            return;

        if (!texDesc.IsValid())
            return;

        var virtPath = texDesc.AssetVirtualPath ?? texDesc.AssetArchiveVirtualPath;
        if (ResourceManager.IsResourceLoaded(virtPath, AccessLevel.AccessGPUOptimizedOnly))
            return;

        var texJob = ResourceManager.CreateNewJob("Loading grass preview textures");
        if (texDesc.AssetArchiveVirtualPath != null)
            texJob.AddLoadArchiveTask(texDesc.AssetArchiveVirtualPath, AccessLevel.AccessGPUOptimizedOnly, false, ResourceType.Flver);
        else if (texDesc.AssetVirtualPath != null)
            texJob.AddLoadFileTask(texDesc.AssetVirtualPath, AccessLevel.AccessGPUOptimizedOnly);

        Task texTask = texJob.Complete();
        if (View.Universe.HasProcessedMapLoad)
            texTask.Wait();
    }

    private Matrix4x4 BuildGrassPreviewTransform(Vector3 centerPosition, Vector3 normal, float previewRadius, GrassPreviewSettings previewSettings, int stampIndex, int instanceIndex)
    {
        var tangent = GetPreviewTangent(normal);
        var bitangent = Vector3.Normalize(Vector3.Cross(normal, tangent));

        var radialSeed = GetPreviewSeed(stampIndex, instanceIndex, 0);
        var angleSeed = GetPreviewSeed(stampIndex, instanceIndex, 1);
        var widthSeed = GetPreviewSeed(stampIndex, instanceIndex, 2);
        var heightSeed = GetPreviewSeed(stampIndex, instanceIndex, 3);
        var yawSeed = GetPreviewSeed(stampIndex, instanceIndex, 4);

        var offsetDistance = MathF.Min(previewSettings.Spacing * (0.35f + radialSeed * 0.65f), previewRadius * 0.9f);
        var offsetAngle = angleSeed * MathF.Tau;
        var offset = (MathF.Cos(offsetAngle) * tangent + MathF.Sin(offsetAngle) * bitangent) * offsetDistance;

        var widthScale = Lerp(previewSettings.WidthScaleMin, previewSettings.WidthScaleMax, widthSeed) * (float)Math.Clamp(previewRadius * 0.55f, 0.35f, 1.25f);
        var heightScale = Lerp(previewSettings.HeightScaleMin, previewSettings.HeightScaleMax, heightSeed) * (float)Math.Clamp(previewRadius * 0.75f, 0.45f, 1.75f);
        var upLift = normal * MathF.Max(heightScale * 0.02f, 0.01f);

        var targetPosition = centerPosition + offset + upLift;
        var alignRotation = CreateAlignmentRotation(normal);
        var yawRotation = Quaternion.CreateFromAxisAngle(normal, ResolvePreviewYaw(previewSettings, yawSeed));
        var rotation = Quaternion.Normalize(yawRotation * alignRotation);

        return Matrix4x4.CreateScale(widthScale, heightScale, widthScale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(targetPosition);
    }

    private float ResolvePreviewYaw(GrassPreviewSettings previewSettings, float yawSeed)
    {
        if (previewSettings.OrientationAngle < 0.0f)
        {
            return yawSeed * MathF.Tau;
        }

        var baseAngle = MathF.PI / 180.0f * previewSettings.OrientationAngle;
        if (previewSettings.OrientationRange <= 0.0f)
        {
            return baseAngle;
        }

        var halfRange = MathF.PI / 180.0f * previewSettings.OrientationRange * 0.5f;
        return baseAngle + ((yawSeed * 2.0f) - 1.0f) * halfRange;
    }

    private Quaternion CreateAlignmentRotation(Vector3 normal)
    {
        var dot = (float)Math.Clamp(Vector3.Dot(Vector3.UnitY, normal), -1.0f, 1.0f);
        if (dot > 0.9999f)
        {
            return Quaternion.Identity;
        }

        if (dot < -0.9999f)
        {
            return Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI);
        }

        var axis = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, normal));
        var angle = MathF.Acos(dot);
        return Quaternion.CreateFromAxisAngle(axis, angle);
    }

    private float GetPreviewSeed(int stampIndex, int instanceIndex, int salt)
    {
        var seed = HashCode.Combine(stampIndex, instanceIndex, salt, _paintGrassParamId);
        seed &= 0x7FFFFFFF;
        return seed / (float)int.MaxValue;
    }

    private float Lerp(float start, float end, float t)
    {
        return start + (end - start) * t;
    }

    private void DisposeGrassPreviewGroups(List<List<MeshRenderableProxy>> previewGroups)
    {
        foreach (var group in previewGroups)
        {
            DisposeGrassPreviewGroup(group);
        }

        previewGroups.Clear();
    }

    private void DisposeGrassPreviewGroup(List<MeshRenderableProxy> group)
    {
        foreach (var preview in group)
        {
            preview.Dispose();
        }

        group.Clear();
    }

    private Color GetHoveredStateBrushColor()
    {
        return _hoveredTargetState switch
        {
            HoveredTargetState.GrassCapableMapPiece => Color.LimeGreen,
            HoveredTargetState.GrassCapableAsset => Color.LimeGreen,
            HoveredTargetState.FilteredOutMapPiece => Color.Goldenrod,
            HoveredTargetState.FilteredOutAsset => Color.Goldenrod,
            HoveredTargetState.UnsupportedDummyAsset => Color.IndianRed,
            HoveredTargetState.UnsupportedTarget => Color.Gray,
            _ => Color.Gray
        };
    }

    private (Vector4 color, string label) GetHoveredStatePresentation()
    {
        return _hoveredTargetState switch
        {
            HoveredTargetState.GrassCapableMapPiece => (new Vector4(0.45f, 0.9f, 0.55f, 1.0f), "Grass-capable map piece"),
            HoveredTargetState.GrassCapableAsset => (new Vector4(0.45f, 0.9f, 0.55f, 1.0f), "Grass-capable asset"),
            HoveredTargetState.FilteredOutMapPiece => (new Vector4(0.95f, 0.8f, 0.35f, 1.0f), "Map piece supports grass, but map piece painting is currently disabled"),
            HoveredTargetState.FilteredOutAsset => (new Vector4(0.95f, 0.8f, 0.35f, 1.0f), "Asset supports grass, but asset painting is currently disabled"),
            HoveredTargetState.UnsupportedDummyAsset => (new Vector4(0.95f, 0.45f, 0.45f, 1.0f), "Unsupported dummy asset: this part type does not serialize grass data"),
            HoveredTargetState.UnsupportedTarget => (new Vector4(0.75f, 0.75f, 0.75f, 1.0f), "Not a grass paint target"),
            _ => (default, null)
        };
    }

    private bool IsViewportPaintingEnabled()
    {
        return _paintEnabled && GrassPaintAdapter.SupportsProject(Project.Descriptor.ProjectType);
    }

    private string GetTargetFilterLabel()
    {
        if (_allowMapPieces && _allowAssets)
        {
            return "Map Pieces + Assets";
        }

        if (_allowMapPieces)
        {
            return "Map Pieces";
        }

        if (_allowAssets)
        {
            return "Assets";
        }

        return "None";
    }
}