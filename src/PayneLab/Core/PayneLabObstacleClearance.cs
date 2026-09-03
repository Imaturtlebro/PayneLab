using UnityEngine;
using Il2CppSLZ.Marrow;

namespace PayneLab
{
    public class PayneLabObstacleClearance
    {
        private readonly PayneLabMod _mod;

        private float _kneeClearanceOffset;
        private float _footClearanceOffset;

        private static PhysicMaterial _slideMaterial;
        private static PhysicMaterial _defaultMaterial;

        private const float MaxMaterialFriction = 1f;

        /// <summary>
        /// Lazily builds the low-friction material used during slides.
        /// On Quest the native object can be destroyed after avatar swaps / level
        /// loads, so we recreate it whenever it has been collected.
        /// </summary>
        private static PhysicMaterial GetSlideMaterial()
        {
            if (_slideMaterial == null)
            {
                _slideMaterial = new PhysicMaterial("PayneLab_Slide");
                _slideMaterial.hideFlags = HideFlags.HideAndDontSave;
            }
            return _slideMaterial;
        }

        private static PhysicMaterial GetDefaultMaterial()
        {
            if (_defaultMaterial == null)
            {
                _defaultMaterial = new PhysicMaterial("PayneLab_Default");
                _defaultMaterial.hideFlags = HideFlags.HideAndDontSave;
            }
            return _defaultMaterial;
        }

        public PayneLabObstacleClearance(PayneLabMod mod) { _mod = mod; }

        public void Tick()
        {
            // Only active during slides
            if (_mod.CurrentState != StuntState.KneeSlide &&
                _mod.CurrentState != StuntState.ButtSlide &&
                _mod.CurrentState != StuntState.DiveProne)
            {
                _kneeClearanceOffset = 0f;
                _footClearanceOffset = 0f;
                return;
            }

            var rig = _mod.CachedRig;
            if (rig == null) return;

            Vector3 velocity = _mod.CachedPelvisRb != null ? _mod.CachedPelvisRb.velocity : Vector3.zero;
            float speed = new Vector2(velocity.x, velocity.z).magnitude;

            if (speed < 0.5f) return;

            // Obstacle probes must stay horizontal: use only the ground-plane
            // component of travel so a downward slam / landing bounce never raycasts
            // into the floor directly below the knees and registers as a fake lip.
            Vector3 moveDir = new Vector3(velocity.x, 0f, velocity.z).normalized;

            // Probe from knee and foot forward along velocity vector
            Vector3 kneePos = _mod.CachedKneeRb != null ? _mod.CachedKneeRb.position : Vector3.zero;
            Vector3 footPos = _mod.CachedFootRb != null ? _mod.CachedFootRb.position : Vector3.zero;

            float probeDist = 0.5f + speed * 0.1f; // longer probe at higher speed

            // Knee probe
            RaycastHit kneeHit;
            if (PayneLabUtils.RaycastNonSelf(_mod, kneePos, moveDir, out kneeHit, probeDist))
            {
                float obstacleHeight = kneeHit.point.y - kneePos.y;
                if (obstacleHeight > 0.05f)
                {
                    _kneeClearanceOffset = Mathf.Lerp(_kneeClearanceOffset, 25f, Time.deltaTime * 10f);
                }
                else
                {
                    _kneeClearanceOffset = Mathf.Lerp(_kneeClearanceOffset, 0f, Time.deltaTime * 5f);
                }
            }
            else
            {
                _kneeClearanceOffset = Mathf.Lerp(_kneeClearanceOffset, 0f, Time.deltaTime * 5f);
            }

            // Foot probe
            RaycastHit footHit;
            if (PayneLabUtils.RaycastNonSelf(_mod, footPos, moveDir, out footHit, probeDist))
            {
                float obstacleHeight = footHit.point.y - footPos.y;
                if (obstacleHeight > 0.05f)
                {
                    _footClearanceOffset = Mathf.Lerp(_footClearanceOffset, 20f, Time.deltaTime * 10f);
                }
                else
                {
                    _footClearanceOffset = Mathf.Lerp(_footClearanceOffset, 0f, Time.deltaTime * 5f);
                }
            }
            else
            {
                _footClearanceOffset = Mathf.Lerp(_footClearanceOffset, 0f, Time.deltaTime * 5f);
            }
        }

        public float GetKneeClearance()
        {
            return _kneeClearanceOffset;
        }

        public float GetFootClearance()
        {
            return _footClearanceOffset;
        }

        /// <summary>
        /// Temporarily switch the pelvis/knee/foot colliders to a shared low-friction
        /// material during slides. Call when entering a slide state.
        /// Every Unity call is guarded: on Quest the material's native object can be
        /// destroyed, and even touching it then throws about once per FixedUpdate,
        /// which tanks framerate. We rebuild-and-retry once instead.
        /// </summary>
        public void SetSlideFriction(float friction)
        {
            if (_mod.CachedRig == null) return;

            float f = Mathf.Clamp(friction, 0f, MaxMaterialFriction);
            SetColliderMaterial(_mod.CachedPelvisRb, TrySetSlideFriction(f));
            SetColliderMaterial(_mod.CachedKneeRb, TrySetSlideFriction(f));
            SetColliderMaterial(_mod.CachedFootRb, TrySetSlideFriction(f));
        }

        /// <summary>
        /// Restore standard friction. Call when exiting a slide state.
        /// </summary>
        public void RestoreFriction()
        {
            if (_mod.CachedRig == null) return;

            try
            {
                SetColliderMaterial(_mod.CachedPelvisRb, GetDefaultMaterial());
                SetColliderMaterial(_mod.CachedKneeRb, GetDefaultMaterial());
                SetColliderMaterial(_mod.CachedFootRb, GetDefaultMaterial());
            }
            catch (System.Exception)
            {
            }
        }

        /// <summary>
        /// Returns a slide material with the requested friction, or null if the
        /// material's native object is broken (never throws).
        /// </summary>
        private static PhysicMaterial TrySetSlideFriction(float friction)
        {
            var material = GetSlideMaterial();
            if (material == null) return null;

            try
            {
                material.staticFriction = friction;
                material.dynamicFriction = friction;
                return material;
            }
            catch (System.Exception)
            {
                // Native material destroyed/unsupported: drop it so it gets recreated
                // on the next call instead of throwing every FixedUpdate.
                _slideMaterial = null;
                return null;
            }
        }

        private void SetColliderMaterial(Rigidbody rb, PhysicMaterial material)
        {
            if (rb == null || material == null) return;

            try
            {
                var collider = rb.GetComponent<Collider>();
                if (collider == null) return;

                collider.material = material;
            }
            catch (System.Exception)
            {
            }
        }
    }
}