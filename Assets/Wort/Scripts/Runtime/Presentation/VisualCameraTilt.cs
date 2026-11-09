using UnityEngine;

[ExecuteAlways]
[DisallowMultipleComponent]
public sealed class VisualCameraTilt : MonoBehaviour
{
    [SerializeField, Tooltip("A dedicated, unrotated child pivot at the base of the visual model.")]
    private Transform visualPivot;

    [SerializeField, Tooltip("Uses the Main Camera when left empty.")]
    private Camera referenceCamera;

    [SerializeField, Range(-30f, 30f), Tooltip("Positive tilt exposes more of the front in the Wort top-down projection.")]
    private float tiltDegrees = 12f;

    private void OnEnable()
    {
        ApplyTilt();
    }

    private void LateUpdate()
    {
        ApplyTilt();
    }

    private void OnDisable()
    {
        SetPivotRotation(Quaternion.identity);
    }

    private void ApplyTilt()
    {
        Camera camera = referenceCamera ? referenceCamera : Camera.main;
        Vector3 tiltAxis = camera ? transform.InverseTransformDirection(camera.transform.right) : Vector3.right;
        SetPivotRotation(Quaternion.AngleAxis(tiltDegrees, tiltAxis));
    }

    private void SetPivotRotation(Quaternion rotation)
    {
        if (visualPivot && visualPivot.parent == transform && Quaternion.Angle(visualPivot.localRotation, rotation) > 0.01f)
            visualPivot.localRotation = rotation;
    }
}
