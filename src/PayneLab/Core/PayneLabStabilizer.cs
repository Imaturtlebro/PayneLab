using UnityEngine;
using Il2CppSLZ.Marrow;

namespace PayneLab
{
    public class PayneLabStabilizer
    {
        private readonly PayneLabMod _mod;

        private const float AimRollDamping = 40f;
        private const float AimRollSpeedThreshold = 0.5f;

        public PayneLabStabilizer(PayneLabMod mod) { _mod = mod; }

        public void Tick()
        {
            // Stabilizer only active when not in full ragdoll
            if (_mod.ActiveInput == InputMode.FullRagdoll) return;

            // Don't fight hard during inverted / action states
            if (_mod.CurrentState == StuntState.Cannonball) return;
            if (_mod.CurrentState == StuntState.Kick) return;
            if (_mod.CurrentState == StuntState.GroundRoll) return;

            var rig = _mod.CachedRig;
            if (rig == null) return;

            Rigidbody spine = _mod.CachedSpineRb ?? _mod.CachedPelvisRb;
            if (spine == null) return;

            // NaN guard: if the sim ever produces infinities (torque/dive explosion
            // shows as pos=NaN), bail so we can't push more NaN into the solver.
            Vector3 spineUp = spine.transform.up;
            Vector3 targetUp = Vector3.up;
            if (!PayneLabUtils.IsFinite(spineUp) || !PayneLabUtils.IsFinite(targetUp)) return;

            // Wallrun torso bank: lean ~15deg away from the wall (Titanfall/Mirror's Edge)
            if (_mod.CurrentState == StuntState.WallRun && _mod.StateMachine.WallNormal.sqrMagnitude > 0.01f)
            {
                Vector3 away = -_mod.StateMachine.WallNormal;
                away.y = 0f;
                if (away.sqrMagnitude < 0.01f) away = Vector3.forward;
                away.Normalize();
                targetUp = (Vector3.up + away * Mathf.Tan(15f * Mathf.Deg2Rad)).normalized;
            }

            // Angle error (signed via cross product magnitude / dot)
            Vector3 correctionAxis = Vector3.Cross(spineUp, targetUp);
            float sinAngle = correctionAxis.magnitude;
            float cosAngle = Vector3.Dot(spineUp, targetUp);
            float angle = Mathf.Atan2(sinAngle, cosAngle) * Mathf.Rad2Deg;

            if (sinAngle > 0.001f)
            {
                correctionAxis.Normalize();
            }
            else
            {
                // Already perfectly upright
                if (cosAngle < 0f)
                    correctionAxis = spine.transform.right; // Upside-down fallback
                else
                    return;
            }

            // Stiffness scaling based on state
            float stateScale = GetStateStiffnessScale();

            // PD spring upright recovery: tau = kp * theta - kd * omega
            float kp = _mod.StabilizerSpring * stateScale;
            float kd = _mod.StabilizerDamping * stateScale;

            // Project angular velocity onto the correction axis so we resist
            // motion AWAY from upright and allow motion TOWARD it
            float angularVelOnAxis = Vector3.Dot(spine.angularVelocity, correctionAxis);
            float torqueMag = (kp * angle * Mathf.Deg2Rad) - (kd * angularVelOnAxis);

            // Clamp to max torque (high-momentum motion overpowers it)
            torqueMag = Mathf.Clamp(torqueMag, -_mod.StabilizerMaxTorque, _mod.StabilizerMaxTorque);

            // NaN guard: never feed a non-finite torque into the solver.
            if (float.IsNaN(torqueMag) || float.IsInfinity(torqueMag) || !PayneLabUtils.IsFinite(correctionAxis))
            {
                return;
            }

            // Apply
            if (Mathf.Abs(torqueMag) > 0.01f)
            {
                spine.AddTorque(correctionAxis * torqueMag, ForceMode.Acceleration);
            }

            // Tactical aim stability: when lying down and moving slow, kill the
            // log-roll spin about the spine's roll axis so 1/2-handed aiming is stable.
            ApplySlowAimRollDamping();
        }

        private void ApplySlowAimRollDamping()
        {
            if (_mod.CurrentState != StuntState.SideLying && _mod.CurrentState != StuntState.DiveProne) return;

            var rb = _mod.CachedPelvisRb ?? _mod.CachedSpineRb;
            if (rb == null) return;

            Vector3 flatVel = new Vector3(rb.velocity.x, 0f, rb.velocity.z);
            if (!PayneLabUtils.IsFinite(rb.velocity) || flatVel.magnitude > AimRollSpeedThreshold) return;

            var spine = _mod.CachedSpineRb ?? rb;
            Vector3 rollAxis = spine.transform.forward;
            Vector3 angVel = spine.angularVelocity;
            float spin = Vector3.Dot(angVel, rollAxis);

            if (Mathf.Abs(spin) < 0.01f) return;

            spine.AddTorque(-rollAxis * (spin * AimRollDamping), ForceMode.Acceleration);
        }

        private float GetStateStiffnessScale()
        {
            switch (_mod.CurrentState)
            {
                case StuntState.Standing:
                    return 1.0f;     // Full stabilizer
                case StuntState.KneeSlide:
                case StuntState.ButtSlide:
                    return 0.4f;     // Gentle - let slide feel natural
                case StuntState.SideLying:
                case StuntState.DiveProne:
                case StuntState.GroundRoll:
                case StuntState.Cannonball:
                    return 0.0f;     // Complete OFF - never torque a prone/grounded body
                case StuntState.Aerial:
                    return 0.25f;    // Low - gentle recovery in air only
                case StuntState.WallRun:
                    return 0.7f;     // High - hold the banked lean against the wall
                case StuntState.Kick:
                    return 0.1f;     // Low during kick
                case StuntState.GetUp:
                    return 1.0f;     // Full push to stand
                default:
                    return 1.0f;
            }
        }
    }
}