using UnityEngine;

/// <summary>
/// Applies a uniform vertical shear to an orthographic camera projection.
/// This changes the rendered view without changing scene geometry or materials.
/// </summary>
[ExecuteAlways]
[RequireComponent(typeof(Camera))]
public sealed class WortShearCamera : MonoBehaviour
{
    [SerializeField, Range(-2f, 2f)]
    private float shear = 1f;

    private Camera targetCamera;

    private Camera TargetCamera
    {
        get
        {
            if (targetCamera == null)
            {
                targetCamera = GetComponent<Camera>();
            }

            return targetCamera;
        }
    }

    private void OnEnable()
    {
        ApplyProjection();
    }

    private void LateUpdate()
    {
        ApplyProjection();
    }

    private void OnValidate()
    {
        if (isActiveAndEnabled)
        {
            ApplyProjection();
        }
    }

    private void ApplyProjection()
    {
        Camera cameraToModify = TargetCamera;
        if (cameraToModify == null || cameraToModify.orthographicSize <= 0f)
        {
            return;
        }

        cameraToModify.orthographic = true;

        float halfHeight = cameraToModify.orthographicSize;
        float halfWidth = halfHeight * cameraToModify.aspect;

        Matrix4x4 projection = Matrix4x4.Ortho(
            -halfWidth,
            halfWidth,
            -halfHeight,
            halfHeight,
            cameraToModify.nearClipPlane,
            cameraToModify.farClipPlane);

        // Move the projected image horizontally as world height increases.
        projection.m12 = projection.m11 * shear;

        // Keep the ground plane visually anchored when the camera has height.
        projection.m13 += projection.m12 * transform.position.y;

        cameraToModify.projectionMatrix = projection;
    }

    private void OnDisable()
    {
        if (targetCamera != null)
        {
            targetCamera.ResetProjectionMatrix();
        }
    }
}
