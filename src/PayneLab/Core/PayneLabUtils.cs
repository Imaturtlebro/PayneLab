using BoneLib;
using UnityEngine;
using System.Collections.Generic;

namespace PayneLab
{
    public static class PayneLabUtils
    {
        private static readonly RaycastHit[] _raycastBuffer = new RaycastHit[64];
        private static readonly Collider[] _overlapBuffer = new Collider[32];

        // Physics.AllLayers. Never rely on DefaultRaycastLayers / LayerMask.GetMask:
        // both misbehave on the stripped Android/Quest build (GetMask throws, and
        // DefaultRaycastLayers can resolve to an invalid mask that makes every
        // physics query return zero hits).
        private const int AllLayers = -1;

        /// <summary>
        /// Raycast that ignores trigger colliders (Marrow volume triggers) and any
        /// collider belonging to the player's own physics rig (thigh/knee/feet).
        /// </summary>
        public static bool RaycastNonSelf(PayneLabMod mod, Vector3 origin, Vector3 direction, out RaycastHit hit, float maxDistance)
        {
            hit = default;
            if (maxDistance <= 0f) return false;

            int count = Physics.RaycastNonAlloc(origin, direction, _raycastBuffer, maxDistance,
                AllLayers, QueryTriggerInteraction.Ignore);
            if (count <= 0) return false;

            float best = maxDistance;
            bool found = false;

            for (int i = 0; i < count; i++)
            {
                RaycastHit h = _raycastBuffer[i];
                if (h.collider == null) continue;
                if (h.distance <= 0.001f) continue;
                if (mod.CachedSelfColliders != null && mod.CachedSelfColliders.Contains(h.collider.GetInstanceID()))
                    continue;

                if (h.distance < best)
                {
                    best = h.distance;
                    hit = h;
                    found = true;
                }
            }

            return found;
        }

        /// <summary>
        /// Swept capsule (a thick ray) that ignores the player's own rig colliders.
        /// Sweeps are far more tolerant of single-sided mesh floors than plain rays.
        /// </summary>
        public static bool CapsuleCastNonSelf(PayneLabMod mod, Vector3 top, Vector3 bottom, float radius,
            Vector3 direction, float maxDistance, out RaycastHit hit)
        {
            hit = default;
            if (maxDistance <= 0f) return false;

            int count = Physics.CapsuleCastNonAlloc(top, bottom, radius, direction, _raycastBuffer,
                maxDistance, AllLayers, QueryTriggerInteraction.Ignore);
            if (count <= 0) return false;

            float best = maxDistance;
            bool found = false;

            for (int i = 0; i < count; i++)
            {
                RaycastHit h = _raycastBuffer[i];
                if (h.collider == null) continue;
                if (h.distance <= 0.001f) continue;
                if (mod.CachedSelfColliders != null && mod.CachedSelfColliders.Contains(h.collider.GetInstanceID()))
                    continue;

                if (h.distance < best)
                {
                    best = h.distance;
                    hit = h;
                    found = true;
                }
            }

            return found;
        }

        /// <summary>
        /// True if any non-player collider overlaps the sphere. Overlap queries
        /// measure actual collision volume instead of scanning surfaces, so they
        /// still detect a floor/ground the player rests on even when that collider
        /// rejects raycasts (backface-culled convex meshes, slanted surfaces, etc).
        /// </summary>
        public static bool HasNonSelfOverlap(PayneLabMod mod, Vector3 center, float radius)
        {
            if (radius <= 0f) return false;

            int count = Physics.OverlapSphereNonAlloc(center, radius, _overlapBuffer, AllLayers,
                QueryTriggerInteraction.Ignore);
            if (count <= 0) return false;

            for (int i = 0; i < count; i++)
            {
                var c = _overlapBuffer[i];
                if (c == null) continue;
                if (mod.CachedSelfColliders != null && mod.CachedSelfColliders.Contains(c.GetInstanceID()))
                    continue;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Non-self overlap result that also reports the closest surface normal,
        /// used for detecting walls without depending on raycasts.
        /// </summary>
        public static bool OverlapNormal(PayneLabMod mod, Vector3 center, float radius, out Vector3 normal)
        {
            normal = Vector3.zero;
            if (radius <= 0f) return false;

            int count = Physics.OverlapSphereNonAlloc(center, radius, _overlapBuffer, AllLayers,
                QueryTriggerInteraction.Ignore);
            if (count <= 0) return false;

            float bestDist = float.MaxValue;
            bool found = false;

            for (int i = 0; i < count; i++)
            {
                var c = _overlapBuffer[i];
                if (c == null) continue;
                if (mod.CachedSelfColliders != null && mod.CachedSelfColliders.Contains(c.GetInstanceID()))
                    continue;

                Vector3 closest = c.ClosestPoint(center);
                Vector3 toCenter = center - closest;
                float d = toCenter.magnitude;
                if (d < 0.0001f) continue;

                if (d < bestDist)
                {
                    bestDist = d;
                    normal = toCenter / d;
                    found = true;
                }
            }

            return found;
        }
        public static bool IsFinite(Vector3 v)
        {
            return !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z) &&
                   !float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z);
        }

        public static Vector3 GetFlatForward()
        {
            if (Player.RigManager == null || Player.RigManager.physicsRig == null)
                return Vector3.forward;

            Vector3 fwd = Player.RigManager.physicsRig.m_head.transform.forward;
            fwd.y = 0f;

            if (fwd.sqrMagnitude > 0.001f)
                fwd.Normalize();
            else
                fwd = Vector3.forward;

            return fwd;
        }

        public static Rigidbody FindConnectedBody(ConfigurableJoint joint, string nameContains)
        {
            if (joint == null) return null;

            if (joint.connectedBody != null)
            {
                if (joint.connectedBody.gameObject.name.ToLower().Contains(nameContains.ToLower()))
                    return joint.connectedBody;
            }

            return null;
        }

        public static float AngleAroundAxis(Vector3 v1, Vector3 v2, Vector3 axis)
        {
            Vector3 proj1 = v1 - Vector3.Dot(v1, axis) * axis;
            Vector3 proj2 = v2 - Vector3.Dot(v2, axis) * axis;
            float angle = Mathf.Acos(Mathf.Clamp(Vector3.Dot(proj1.normalized, proj2.normalized), -1f, 1f)) * Mathf.Rad2Deg;
            return angle * Mathf.Sign(Vector3.Dot(axis, Vector3.Cross(proj1, proj2)));
        }
    }
}
