﻿using Hexa.NET.ImGui;
using StudioCore.Configuration;
using StudioCore.Core;
using StudioCore.Editor;
using StudioCore.Editors.TimeActEditor.Core;
using StudioCore.Editors.TimeActEditor.Tools;
using StudioCore.Interface;
using System.Numerics;

namespace StudioCore.Editors.TimeActEditor;

public class TimeActEditorScreen : EditorScreen
{
    public Smithbox BaseEditor;
    public ProjectEntry Project;
    public bool FirstFrame { get; set; }

    public bool ShowSaveOption { get; set; }

    public ActionManager EditorActionManager = new();

    public TimeActSelectionManager Selection;
    public TimeActCommandQueue CommandQueue;
    public TimeActActionHandler ActionHandler;
    public TimeActPropertyEditor PropertyEditor;
    public TimeActDecorator Decorator;
    public TimeActShortcuts EditorShortcuts;

    public TimeActToolView ToolView;
    public TimeActToolMenubar ToolMenubar;

    public TimeActContainerFileView ContainerFileView;
    public TimeActInternalFileView InternalFileView;
    public TimeActEventGraphView EventGraphView;
    public TimeActAnimationView AnimationView;
    public TimeActAnimationPropertyView AnimationPropertyView;
    public TimeActEventView EventView;
    public TimeActEventPropertyView EventPropertyView;

    public TimeActEditorScreen(Smithbox baseEditor, ProjectEntry project)
    {
        BaseEditor = baseEditor;
        Project = project;

        ActionHandler = new TimeActActionHandler(this);

        Selection = new TimeActSelectionManager(this);
        Decorator = new TimeActDecorator(this);
        PropertyEditor = new TimeActPropertyEditor(this);
        EditorShortcuts = new TimeActShortcuts(this);
        CommandQueue = new TimeActCommandQueue(this);

        ContainerFileView = new TimeActContainerFileView(this);
        InternalFileView = new TimeActInternalFileView(this);
        EventGraphView = new TimeActEventGraphView(this);
        AnimationView = new TimeActAnimationView(this);
        AnimationPropertyView = new TimeActAnimationPropertyView(this);
        EventView = new TimeActEventView(this);
        EventPropertyView = new TimeActEventPropertyView(this);

        ToolView = new TimeActToolView(this);
        ToolMenubar = new TimeActToolMenubar(this);
    }

    public string EditorName => "Time Act Editor##TimeActEditor";
    public string CommandEndpoint => "timeact";
    public string SaveType => "TAE";
    public string WindowName => "";
    public bool HasDocked { get; set; }

    /// <summary>
    /// The editor main loop
    /// </summary>
    public void OnGUI(string[] initcmd)
    {
        var scale = DPI.GetUIScale();

        // Docking setup
        ImGui.PushStyleColor(ImGuiCol.Text, UI.Current.ImGui_Default_Text_Color);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(4, 4) * scale);
        Vector2 wins = ImGui.GetWindowSize();
        Vector2 winp = ImGui.GetWindowPos();
        winp.Y += 20.0f * scale;
        wins.Y -= 20.0f * scale;
        ImGui.SetNextWindowPos(winp);
        ImGui.SetNextWindowSize(wins);

        var dsid = ImGui.GetID("DockSpace_TimeActEditor");
        ImGui.DockSpace(dsid, new Vector2(0, 0), ImGuiDockNodeFlags.None);

        CommandQueue.Parse(initcmd);

        EditorShortcuts.Monitor();

        if (ImGui.BeginMenuBar())
        {
            FileMenu();
            EditMenu();
            ViewMenu();
            ToolMenu();

            ImGui.EndMenuBar();
        }

        if (CFG.Current.Interface_TimeActEditor_ContainerFileList)
        {
            ContainerFileView.Display();
        }

        if (CFG.Current.Interface_TimeActEditor_TimeActList)
        {
            InternalFileView.Display();
        }

        if (CFG.Current.Interface_TimeActEditor_AnimationList)
        {
            AnimationView.Display();
            //EventGraphView.Display();
        }

        if (CFG.Current.Interface_TimeActEditor_AnimationProperties)
        {
            AnimationPropertyView.Display();
        }

        if (CFG.Current.Interface_TimeActEditor_EventList)
        {
            EventView.Display();
        }

        if (CFG.Current.Interface_TimeActEditor_EventProperties)
        {
            EventPropertyView.Display();
        }

        if (CFG.Current.Interface_TimeActEditor_ToolWindow)
        {
            ToolView.OnGui();
        }

        ActionHandler.OnGui();

        ImGui.PopStyleVar();
        ImGui.PopStyleColor(1);
    }

    public void FileMenu()
    {
        if (ImGui.BeginMenu("File"))
        {
            if (ImGui.MenuItem($"Save", $"{KeyBindings.Current.CORE_Save.HintText}"))
            {
                Save();
            }

            if (ImGui.MenuItem($"Save All", $"{KeyBindings.Current.CORE_SaveAll.HintText}"))
            {
                SaveAll();
            }

            ImGui.EndMenu();
        }
    }


    public void EditMenu()
    {
        if (ImGui.BeginMenu("Edit"))
        {
            // Undo
            if (ImGui.MenuItem($"Undo", $"{KeyBindings.Current.CORE_UndoAction.HintText} / {KeyBindings.Current.CORE_UndoContinuousAction.HintText}"))
            {
                if (EditorActionManager.CanUndo())
                {
                    EditorActionManager.UndoAction();
                }
            }

            // Undo All
            if (ImGui.MenuItem($"Undo All"))
            {
                if (EditorActionManager.CanUndo())
                {
                    EditorActionManager.UndoAllAction();
                }
            }

            // Redo
            if (ImGui.MenuItem($"Redo", $"{KeyBindings.Current.CORE_RedoAction.HintText} / {KeyBindings.Current.CORE_RedoContinuousAction.HintText}"))
            {
                if (EditorActionManager.CanRedo())
                {
                    EditorActionManager.RedoAction();
                }
            }

            ImGui.Separator();

            if (ImGui.MenuItem("Duplicate", KeyBindings.Current.CORE_DuplicateSelectedEntry.HintText))
            {
                ActionHandler.DetermineDuplicateTarget();
            }
            UIHelper.Tooltip($"Duplicates the current selection.");

            if (ImGui.MenuItem("Delete", KeyBindings.Current.CORE_DeleteSelectedEntry.HintText))
            {
                ActionHandler.DetermineDeleteTarget();
            }
            UIHelper.Tooltip($"Deletes the current selection.");

            ImGui.EndMenu();
        }
    }

    public void ViewMenu()
    {
        if (ImGui.BeginMenu("View"))
        {
            if (ImGui.MenuItem("TAE Files"))
            {
                CFG.Current.Interface_TimeActEditor_ContainerFileList = !CFG.Current.Interface_TimeActEditor_ContainerFileList;
            }
            UIHelper.ShowActiveStatus(CFG.Current.Interface_TimeActEditor_ContainerFileList);

            if (ImGui.MenuItem("Time Acts"))
            {
                CFG.Current.Interface_TimeActEditor_TimeActList = !CFG.Current.Interface_TimeActEditor_TimeActList;
            }
            UIHelper.ShowActiveStatus(CFG.Current.Interface_TimeActEditor_TimeActList);

            if (ImGui.MenuItem("Animations"))
            {
                CFG.Current.Interface_TimeActEditor_AnimationList = !CFG.Current.Interface_TimeActEditor_AnimationList;
            }
            UIHelper.ShowActiveStatus(CFG.Current.Interface_TimeActEditor_AnimationList);

            if (ImGui.MenuItem("Animation Properties"))
            {
                CFG.Current.Interface_TimeActEditor_AnimationProperties = !CFG.Current.Interface_TimeActEditor_AnimationProperties;
            }
            UIHelper.ShowActiveStatus(CFG.Current.Interface_TimeActEditor_AnimationProperties);

            if (ImGui.MenuItem("Events"))
            {
                CFG.Current.Interface_TimeActEditor_EventList = !CFG.Current.Interface_TimeActEditor_EventList;
            }
            UIHelper.ShowActiveStatus(CFG.Current.Interface_TimeActEditor_EventList);

            if (ImGui.MenuItem("Event Properties"))
            {
                CFG.Current.Interface_TimeActEditor_EventProperties = !CFG.Current.Interface_TimeActEditor_EventProperties;
            }
            UIHelper.ShowActiveStatus(CFG.Current.Interface_TimeActEditor_EventProperties);

            if (ImGui.MenuItem("Tool Window"))
            {
                CFG.Current.Interface_TimeActEditor_ToolWindow = !CFG.Current.Interface_TimeActEditor_ToolWindow;
            }
            UIHelper.ShowActiveStatus(CFG.Current.Interface_TimeActEditor_ToolWindow);

            ImGui.EndMenu();
        }
    }

    /// <summary>
    /// Handle the editor menubar
    /// </summary>
    public void ToolMenu()
    {
        // ToolMenubar.DisplayMenu();
    }

    public void Save()
    {
        if (Project.ProjectType is ProjectType.AC6)
        {
            TaskLogs.AddLog("Saving is not supported for AC6 projects currently.");
            return;
        }

        Project.TimeActData.PrimaryCharacterBank.SaveTimeActTask(Selection.ContainerInfo, Selection.ContainerBinder);

        // Save the configuration JSONs
        BaseEditor.SaveConfiguration();
    }

    public async void SaveAll()
    {
        if (Project.ProjectType is ProjectType.AC6)
        {
            TaskLogs.AddLog("Saving is not supported for AC6 projects currently.");
            return;
        }

        await Project.TimeActData.PrimaryCharacterBank.SaveTimeActsTask();

        // Save the configuration JSONs
        BaseEditor.SaveConfiguration();
    }
}
