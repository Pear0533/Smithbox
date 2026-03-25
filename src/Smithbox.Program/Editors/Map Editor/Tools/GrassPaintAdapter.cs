using SoulsFormats;
using StudioCore.Application;
using StudioCore.Editors.Common;
using System;
using System.Linq;
using System.Reflection;

namespace StudioCore.Editors.MapEditor;

public sealed class GrassPaintTarget
{
    private readonly object _owner;
    private readonly PropertyInfo _grassProperty;
    private readonly Func<object, object> _cloneGrassData;
    private readonly Func<object> _createDefaultGrassData;
    private readonly Func<object, int[]> _readSlots;
    private readonly Action<object, int[]> _writeSlots;

    public GrassPaintTarget(Entity entity, object owner, PropertyInfo grassProperty, Func<object, object> cloneGrassData,
        Func<object> createDefaultGrassData, Func<object, int[]> readSlots, Action<object, int[]> writeSlots)
    {
        Entity = entity;
        _owner = owner;
        _grassProperty = grassProperty;
        _cloneGrassData = cloneGrassData;
        _createDefaultGrassData = createDefaultGrassData;
        _readSlots = readSlots;
        _writeSlots = writeSlots;
    }

    public Entity Entity { get; }

    public int[] ReadSlots()
    {
        return ReadSlots(GetCurrentGrassData());
    }

    public PropertiesChangedAction CreateApplyAction(int[] nextSlots)
    {
        var currentGrassData = GetCurrentGrassData();
        var currentSlots = ReadSlots(currentGrassData);

        if (currentSlots.SequenceEqual(nextSlots))
        {
            return null;
        }

        var nextGrassData = currentGrassData != null ? _cloneGrassData(currentGrassData) : _createDefaultGrassData();
        _writeSlots(nextGrassData, nextSlots);

        var action = new PropertiesChangedAction(_grassProperty, _owner, nextGrassData, Entity.Name);
        action.SetPostExecutionAction(_ => Entity.UpdateRenderModel());
        return action;
    }

    private object GetCurrentGrassData()
    {
        return _grassProperty.GetValue(_owner);
    }

    private int[] ReadSlots(object grassData)
    {
        if (grassData == null)
        {
            return _readSlots(_createDefaultGrassData());
        }

        return _readSlots(grassData);
    }
}

public static class GrassPaintAdapter
{
    public static bool SupportsProject(ProjectType projectType)
    {
        return projectType is ProjectType.ER or ProjectType.AC6 or ProjectType.NR;
    }

    public static bool TryCreate(Entity entity, out GrassPaintTarget target)
    {
        target = null;

        if (entity == null || entity.WrappedObject == null)
        {
            return false;
        }

        switch (entity.WrappedObject)
        {
            case MSBE.Part.MapPiece mapPiece:
                target = CreateErTarget(entity, mapPiece, nameof(MSBE.Part.MapPiece.GrassConfigStruct));
                return true;
            case MSBE.Part.Asset asset:
                target = CreateErTarget(entity, asset, nameof(MSBE.Part.Asset.GrassConfigStruct));
                return true;
            case MSB_AC6.Part.MapPiece mapPiece:
                target = CreateAc6Target(entity, mapPiece, nameof(MSB_AC6.Part.MapPiece.GrassConfigStruct));
                return true;
            case MSB_AC6.Part.Asset asset:
                target = CreateAc6Target(entity, asset, nameof(MSB_AC6.Part.Asset.GrassConfigStruct));
                return true;
            case MSB_NR.Part.MapPiece mapPiece:
                target = CreateNrTarget(entity, mapPiece, nameof(MSB_NR.Part.MapPiece.GrassData));
                return true;
            case MSB_NR.Part.Asset asset:
                target = CreateNrTarget(entity, asset, nameof(MSB_NR.Part.Asset.GrassData));
                return true;
            default:
                return false;
        }
    }

    private static GrassPaintTarget CreateErTarget(Entity entity, object owner, string propertyName)
    {
        var grassProperty = owner.GetType().GetProperty(propertyName);
        return new GrassPaintTarget(entity, owner, grassProperty,
            grassData => ((MSBE.Part.GrassConfigStruct)grassData).DeepCopy(),
            () => new MSBE.Part.GrassConfigStruct(),
            grassData =>
            {
                var config = (MSBE.Part.GrassConfigStruct)grassData;
                return
                [
                    config.GrassParamId0,
                    config.GrassParamId1,
                    config.GrassParamId2,
                    config.GrassParamId3,
                    config.GrassParamId4,
                    config.GrassParamId5
                ];
            },
            (grassData, slots) =>
            {
                var config = (MSBE.Part.GrassConfigStruct)grassData;
                config.GrassParamId0 = slots[0];
                config.GrassParamId1 = slots[1];
                config.GrassParamId2 = slots[2];
                config.GrassParamId3 = slots[3];
                config.GrassParamId4 = slots[4];
                config.GrassParamId5 = slots[5];
            });
    }

    private static GrassPaintTarget CreateAc6Target(Entity entity, object owner, string propertyName)
    {
        var grassProperty = owner.GetType().GetProperty(propertyName);
        return new GrassPaintTarget(entity, owner, grassProperty,
            grassData => ((MSB_AC6.Part.GrassConfigStruct)grassData).DeepCopy(),
            () => new MSB_AC6.Part.GrassConfigStruct(),
            grassData => ((MSB_AC6.Part.GrassConfigStruct)grassData).GrassTypeParamIds.ToArray(),
            (grassData, slots) =>
            {
                var config = (MSB_AC6.Part.GrassConfigStruct)grassData;
                for (var index = 0; index < slots.Length; index++)
                {
                    config.GrassTypeParamIds[index] = slots[index];
                }
            });
    }

    private static GrassPaintTarget CreateNrTarget(Entity entity, object owner, string propertyName)
    {
        var grassProperty = owner.GetType().GetProperty(propertyName);
        return new GrassPaintTarget(entity, owner, grassProperty,
            grassData => ((MSB_NR.Part.GrassStruct)grassData).DeepCopy(),
            () => new MSB_NR.Part.GrassStruct(),
            grassData => ((MSB_NR.Part.GrassStruct)grassData).GrassParamIds.ToArray(),
            (grassData, slots) =>
            {
                var config = (MSB_NR.Part.GrassStruct)grassData;
                for (var index = 0; index < slots.Length; index++)
                {
                    config.GrassParamIds[index] = slots[index];
                }
            });
    }
}