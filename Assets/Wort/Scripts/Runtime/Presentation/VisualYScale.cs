using System;
using UnityEngine;

[ExecuteAlways]
[DisallowMultipleComponent]
public sealed class VisualYScale : MonoBehaviour
{
    [SerializeField, Tooltip("Optional override. Leave empty to find the visuals and keep their base anchored automatically.")]
    private Transform visualPivot;

    [SerializeField, Range(0.1f, 1f), Tooltip("Fraction of the original visual height. 1 restores full height.")]
    private float heightMultiplier = 0.8f;

    [SerializeField, HideInInspector] private Transform originalTarget;
    [SerializeField, HideInInspector] private float originalY = 1f;
    [SerializeField, HideInInspector] private bool originalWasAutomatic;
    [SerializeField, HideInInspector] private int originalAxis = 1;
    [SerializeField, HideInInspector] private float anchorY;

    private Transform automaticTarget;
    private int automaticAxis;
    private Renderer[] renderers;

    private void OnEnable()
    {
        automaticTarget = null;
        ApplyScale();
    }

    private void Update()
    {
        ApplyScale();
    }

    private void OnDisable()
    {
        SetScale(originalTarget, originalAxis, originalY, anchorY);
    }

    private void OnTransformChildrenChanged()
    {
        automaticTarget = null;
    }

    private void ApplyScale()
    {
        bool automatic = !visualPivot;
        Transform target = automatic ? GetAutomaticTarget() : visualPivot;
        int axis = automatic ? automaticAxis : 1;
        if (target != transform && !target.IsChildOf(transform))
        {
            SetScale(originalTarget, originalAxis, originalY, anchorY);
            originalTarget = null;
            return;
        }

        if (target != originalTarget || automatic != originalWasAutomatic || axis != originalAxis)
        {
            SetScale(originalTarget, originalAxis, originalY, anchorY);
            originalTarget = target;
            originalAxis = axis;
            originalY = target.localScale[axis];
            originalWasAutomatic = automatic;
            anchorY = automatic ? FindBottom(target, axis) : 0f;
        }

        SetScale(target, axis, originalY * heightMultiplier, anchorY);
    }

    private Transform GetAutomaticTarget()
    {
        if (automaticTarget)
            return automaticTarget;

        renderers = Array.FindAll(GetComponentsInChildren<Renderer>(true),
            renderer => renderer is MeshRenderer || renderer is SkinnedMeshRenderer || renderer is SpriteRenderer);
        automaticTarget = FindVisualBranch();
        automaticAxis = FindVerticalAxis(automaticTarget);
        return automaticTarget;
    }

    private Transform FindVisualBranch()
    {
        if (renderers.Length == 0 || renderers[0].transform == transform)
            return transform;

        Transform branch = renderers[0].transform;
        while (branch.parent != transform)
            branch = branch.parent;

        foreach (Renderer renderer in renderers)
        {
            if (renderer.transform != branch && !renderer.transform.IsChildOf(branch))
                return transform;
        }

        if (!branch.GetComponentInChildren<Collider>(true) && !branch.GetComponentInChildren<Rigidbody>(true))
            return branch;
        return transform;
    }

    private static int FindVerticalAxis(Transform target)
    {
        int axis = 1;
        float alignment = Mathf.Abs(target.up.y);
        if (Mathf.Abs(target.right.y) > alignment)
        {
            axis = 0;
            alignment = Mathf.Abs(target.right.y);
        }
        if (Mathf.Abs(target.forward.y) > alignment)
            axis = 2;
        return axis;
    }

    private float FindBottom(Transform target, int axis)
    {
        if (renderers.Length == 0 || Mathf.Approximately(target.localScale[axis], 0f))
            return 0f;

        Vector3 direction = Vector3.zero;
        direction[axis] = 1f;
        float sign = target.TransformVector(direction).y < 0f ? -1f : 1f;
        float bottom = float.PositiveInfinity;
        foreach (Renderer renderer in renderers)
        {
            Bounds bounds = renderer.localBounds;
            Matrix4x4 toTarget = target.worldToLocalMatrix * renderer.localToWorldMatrix;
            Vector3 center = toTarget.MultiplyPoint3x4(bounds.center);
            float extent = Mathf.Abs(toTarget[axis, 0]) * bounds.extents.x +
                           Mathf.Abs(toTarget[axis, 1]) * bounds.extents.y +
                           Mathf.Abs(toTarget[axis, 2]) * bounds.extents.z;
            bottom = Mathf.Min(bottom, sign * center[axis] - extent);
        }
        return sign * bottom;
    }

    private static void SetScale(Transform target, int axis, float value, float anchor)
    {
        if (!target)
            return;

        Vector3 scale = target.localScale;
        if (scale[axis] == value)
            return;
        Vector3 offset = Vector3.zero;
        offset[axis] = (scale[axis] - value) * anchor;
        if (offset[axis] != 0f)
            target.localPosition += target.localRotation * offset;
        scale[axis] = value;
        target.localScale = scale;
    }
}
