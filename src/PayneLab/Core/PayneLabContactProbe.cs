using UnityEngine;

namespace PayneLab
{
    /// <summary>
    /// Runtime-attached collision listener. The world floors/walls in this
    /// environment reject every Physics query (raycast / capsule / overlap all
    /// return nothing, so gDist stays 999 forever), but real rigidbody contact
    /// obviously still happens - the body stands and slides on the floor. This
    /// component captures that actual physics contact and feeds it back to the
    /// mod, giving bulletproof ground/wall detection without any surface query.
    ///
    /// Static geometry (no rigidbody) is treated as world surface; dynamic bodies
    /// (items, other avatars) are ignored so inventory dropping through doesn't
    /// feed false signals.
    /// </summary>
    public class PayneLabContactProbe : MonoBehaviour
    {
        public PayneLabMod Mod;

        private void OnCollisionStay(Collision collision)
        {
            if (Mod == null) return;

            // World surfaces are static colliders (no rigidbody) or kinematic rigs
            // (moving floors/platforms). Dynamic bodies (items, props, other avatars)
            // are ignored - their contact can't tell us anything about the ground.
            var otherRb = collision.rigidbody;
            if (otherRb != null && !otherRb.isKinematic) return;

            var self = Mod.CachedSelfColliders;
            var other = collision.collider;
            if (self == null || other == null) return;
            if (self.Contains(other.GetInstanceID())) return;

            int contacts = collision.contactCount;
            for (int i = 0; i < contacts; i++)
            {
                var c = collision.GetContact(i);
                if (c.otherCollider == null) continue;
                if (self.Contains(c.otherCollider.GetInstanceID())) continue;
                Mod.ReportContact(c.point, c.normal);
            }
        }
    }
}