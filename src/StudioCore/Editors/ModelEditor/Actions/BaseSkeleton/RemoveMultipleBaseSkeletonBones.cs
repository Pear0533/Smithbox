﻿using SoulsFormats;
using StudioCore.Editor.Multiselection;
using StudioCore.Editors.MapEditor.Actions.Viewport;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using static SoulsFormats.FLVER2;

namespace StudioCore.Editors.ModelEditor.Actions.BaseSkeleton;

public class RemoveMultipleBaseSkeletonBones : ViewportAction
{
    private ModelEditorScreen Screen;
    private ModelSelectionManager Selection;
    private ModelViewportManager ViewportManager;

    private FLVER2 CurrentFLVER;

    private Multiselection Multiselect;
    private List<FLVER2.SkeletonSet.Bone> RemovedObjects;
    private List<FLVER2.SkeletonSet.Bone> StoredObjects;
    private List<int> StoredIndices;

    public RemoveMultipleBaseSkeletonBones(ModelEditorScreen screen, FLVER2 flver, Multiselection multiselect)
    {
        Screen = screen;
        Selection = screen.Selection;
        ViewportManager = screen.ViewportManager;

        CurrentFLVER = flver;
        Multiselect = multiselect;
        RemovedObjects = new List<FLVER2.SkeletonSet.Bone>();
        StoredIndices = new List<int>();
        StoredObjects = new List<FLVER2.SkeletonSet.Bone>();
    }

    public override ActionEvent Execute(bool isRedo = false)
    {
        Selection._selectedBaseSkeletonBone = -1;

        foreach (var idx in Multiselect.StoredIndices)
        {
            StoredIndices.Add(idx);

            if (CurrentFLVER.Skeletons.BaseSkeleton[idx] != null)
                RemovedObjects.Add(CurrentFLVER.Skeletons.BaseSkeleton[idx]);
        }

        foreach (var entry in RemovedObjects)
        {
            StoredObjects.Add(entry.Clone());
            CurrentFLVER.Skeletons.BaseSkeleton.Remove(entry);
        }

        Multiselect.StoredIndices = new List<int>();

        return ActionEvent.NoEvent;
    }

    public override ActionEvent Undo()
    {
        for (int i = 0; i < StoredIndices.Count; i++)
        {
            CurrentFLVER.Skeletons.BaseSkeleton.Insert(StoredIndices[i], StoredObjects[i]);
        }

        return ActionEvent.NoEvent;
    }
    public override string GetEditMessage()
    {
        return "";
    }
}