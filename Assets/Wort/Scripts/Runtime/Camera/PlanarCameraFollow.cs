using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(Camera))]
public sealed class PlanarCameraFollow : MonoBehaviour
{
    [SerializeField] private Transform target;
    [SerializeField, Min(0.01f)] private float followRate = 12f;

    private void OnEnable()
    {
        if (target)
            transform.position = GetTargetPosition();
    }

    private void LateUpdate()
    {
        Follow(Time.deltaTime);
    }

    private void Follow(float deltaTime)
    {
        if (!target)
            return;

        float blend = 1f - Mathf.Exp(-followRate * deltaTime);
        transform.position = Vector3.Lerp(transform.position, GetTargetPosition(), blend);
    }

    private Vector3 GetTargetPosition()
    {
        Vector3 position = target.position;
        // 错切相机把高度 y 画成往北移 y × shear（地面 y = 0 对准画面中心）。站在台地上的角色要居中，相机 z 也跟着加上这段（负责人 2026-11-13：相机没对准主角）
        if (shearCamera == null) shearCamera = GetComponent<WortShearCamera>();
        if (shearCamera != null && shearCamera.isActiveAndEnabled)
            position.z += target.position.y * shearCamera.Shear;
        position.y = transform.position.y;
        return position;
    }

    private WortShearCamera shearCamera;
}
