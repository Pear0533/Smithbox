using StudioCore.Editors.Common;
using System.Collections.Generic;

namespace StudioCore.Editors.MapEditor;

public sealed class GrassPaintStrokeAction : ViewportAction
{
    private readonly List<ViewportAction> _actions;
    private readonly string _editMessage;
    private bool _skipInitialExecute;

    public GrassPaintStrokeAction(List<ViewportAction> actions, int entityCount, bool skipInitialExecute)
    {
        _actions = actions;
        _skipInitialExecute = skipInitialExecute;
        _editMessage = $"Grass paint stroke applied to {entityCount} entr{(entityCount == 1 ? "y" : "ies")}";
    }

    public override ActionEvent Execute(bool isRedo = false)
    {
        if (_skipInitialExecute && !isRedo)
        {
            _skipInitialExecute = false;
            return ActionEvent.NoEvent;
        }

        var evt = ActionEvent.NoEvent;
        foreach (var action in _actions)
        {
            evt |= action.Execute(isRedo);
        }

        return evt;
    }

    public override ActionEvent Undo()
    {
        var evt = ActionEvent.NoEvent;

        for (var index = _actions.Count - 1; index >= 0; index--)
        {
            evt |= _actions[index].Undo();
        }

        return evt;
    }

    public override string GetEditMessage()
    {
        return _editMessage;
    }
}