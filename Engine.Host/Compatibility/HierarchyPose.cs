using System;

namespace UnityEngine
{
    public partial class Transform
    {
        // Fold local coordinates through the actual hierarchy. Subtracting a
        // cached immediate-parent world position loses native rounding when
        // large zone translations surround smaller platform-local coordinates.
        private static bool Identity(Quaternion q) => q.x == 0 && q.y == 0 && q.z == 0 && q.w == 1;
        private static Vector3 ToWorld(Transform parent, Vector3 point, int depth = 0)
        {
            if (parent == null) return point;
            if (depth > 128) throw new NotSupportedException("Transform hierarchy is too deep.");
            var scale = parent.m_LocalScale;
            if (scale.x != 1 || scale.y != 1 || scale.z != 1) point = Vector3.Scale(point, scale);
            if (!Identity(parent.m_LocalRotation)) point = parent.m_LocalRotation * point;
            return ToWorld(parent.m_Parent, parent.m_LocalPosition + point, depth + 1);
        }
        private static Vector3 FromWorld(Transform parent, Vector3 point, int depth = 0)
        {
            if (parent == null) return point;
            if (depth > 128) throw new NotSupportedException("Transform hierarchy is too deep.");
            point = FromWorld(parent.m_Parent, point, depth + 1) - parent.m_LocalPosition;
            var q = parent.m_LocalRotation;
            if (!Identity(q))
            {
                float norm = q.x*q.x + q.y*q.y + q.z*q.z + q.w*q.w;
                if (norm == 0) throw new NotSupportedException("Transform has a singular rotation.");
                point = new Quaternion(-q.x/norm, -q.y/norm, -q.z/norm, q.w/norm) * point;
            }
            var scale = parent.m_LocalScale;
            if (scale.x == 0 || scale.y == 0 || scale.z == 0) throw new NotSupportedException("Transform has a singular scale.");
            if (scale.x != 1 || scale.y != 1 || scale.z != 1) point = new Vector3(point.x/scale.x, point.y/scale.y, point.z/scale.z);
            return point;
        }
    }
}
