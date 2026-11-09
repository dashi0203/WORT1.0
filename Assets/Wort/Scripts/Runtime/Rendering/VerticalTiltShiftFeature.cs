using UnityEngine;
using UnityEngine.Rendering.Universal;

public sealed class VerticalTiltShiftFeature : FullScreenPassRendererFeature
{
    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        var cameraData = renderingData.cameraData;
        if (cameraData.cameraType == CameraType.Game && cameraData.postProcessEnabled &&
            cameraData.camera.CompareTag("MainCamera"))
            base.AddRenderPasses(renderer, ref renderingData);
    }
}
