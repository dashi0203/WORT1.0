using System.Collections.Generic;
using UnityEngine;

namespace Wort.Rendering
{
    /// <summary>
    /// 让物体在地面 / 墙上留下环境光遮蔽（WorldAO）：用一个竖直胶囊代表物体的大致形状。
    /// 会动的物体（主角、动物）直接挂；静态物体（树、石头）以后由生成器登记。
    /// 物体本身用 URP Lit 时，它自己不接收这层 AO（只影响地形）。
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("Wort/AO Occluder")]
    public sealed class AOOccluder : MonoBehaviour
    {
        internal static readonly List<AOOccluder> All = new List<AOOccluder>();

        [Tooltip("胶囊半径（米）")]
        [Range(0.05f, 3f)] public float radius = 0.32f;
        [Tooltip("胶囊总高（米，含两头半球），从物体原点往上算")]
        [Range(0.1f, 10f)] public float height = 1.1f;
        [Tooltip("胶囊底部离物体原点的高度（米）")]
        public float bottom = 0f;

        private void OnEnable() { if (!All.Contains(this)) All.Add(this); }
        private void OnDisable() => All.Remove(this);

        /// <summary>胶囊两端球心（世界坐标）和半径。不受物体缩放影响（尺寸直接按米）。</summary>
        public void GetCapsule(out Vector3 a, out Vector3 b, out float r)
        {
            r = radius;
            Vector3 p = transform.position;
            float y0 = p.y + bottom + radius;
            float y1 = p.y + bottom + Mathf.Max(height - radius, radius);
            a = new Vector3(p.x, y0, p.z);
            b = new Vector3(p.x, y1, p.z);
        }

        private void OnDrawGizmosSelected()
        {
            GetCapsule(out Vector3 a, out Vector3 b, out float r);
            Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.6f);
            Gizmos.DrawWireSphere(a, r);
            Gizmos.DrawWireSphere(b, r);
            Gizmos.DrawLine(a + Vector3.right * r, b + Vector3.right * r);
            Gizmos.DrawLine(a - Vector3.right * r, b - Vector3.right * r);
            Gizmos.DrawLine(a + Vector3.forward * r, b + Vector3.forward * r);
            Gizmos.DrawLine(a - Vector3.forward * r, b - Vector3.forward * r);
        }
    }
}
