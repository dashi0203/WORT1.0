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
        position.y = transform.position.y;
        return position;
    }
}
