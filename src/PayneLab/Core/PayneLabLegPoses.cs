using UnityEngine;
using Il2CppSLZ.Marrow;

namespace PayneLab
{
    public class PayneLabLegPoses
    {
        private readonly PayneLabMod _mod;

        // Desired target rotations for each leg joint
        public Quaternion LeftHipTarget = Quaternion.identity;
        public Quaternion RightHipTarget = Quaternion.identity;
        public Quaternion LeftKneeTarget = Quaternion.identity;
        public Quaternion RightKneeTarget = Quaternion.identity;

        // Smoothed / slewed joint targets to prevent instantaneous torque spikes
        private Quaternion _curLeftHip = Quaternion.identity;
        private Quaternion _curRightHip = Quaternion.identity;
        private Quaternion _curLeftKnee = Quaternion.identity;
        private Quaternion _curRightKnee = Quaternion.identity;
        private bool _poseInitialized;
        private const float MaxPoseSlewRate = 420f; // degrees per second

        // Ground-side hysteresis latch for side lying
        private bool _preferRightDown = true;
        private float _proneTuckBlend;

        public PayneLabLegPoses(PayneLabMod mod) { _mod = mod; }

        public void OnStateChanged(StuntState from, StuntState to) { }

        public void ResetPoseTargets()
        {
            _poseInitialized = false;
            _curLeftHip = Quaternion.identity;
            _curRightHip = Quaternion.identity;
            _curLeftKnee = Quaternion.identity;
            _curRightKnee = Quaternion.identity;
        }

        public void Tick()
        {
            if (_mod.ActiveInput == InputMode.FullIK) return;
            if (_mod.CachedRig == null) return;

            // In FullRagdoll mode, joints must remain at neutral bind pose (Quaternion.identity)
            // so the weak limp spring (40f) maintains joint integrity without fighting the physics.
            if (_mod.ActiveInput == InputMode.FullRagdoll)
            {
                LeftHipTarget = Quaternion.identity;
                RightHipTarget = Quaternion.identity;
                LeftKneeTarget = Quaternion.identity;
                RightKneeTarget = Quaternion.identity;

                _curLeftHip = Quaternion.identity;
                _curRightHip = Quaternion.identity;
                _curLeftKnee = Quaternion.identity;
                _curRightKnee = Quaternion.identity;

                ApplyPoseToJoints();
                return;
            }

            // Compute target pose based on current state
            switch (_mod.CurrentState)
            {
                case StuntState.Standing:
                    ComputeStandingPose();
                    break;
                case StuntState.KneeSlide:
                    ComputeKneeSlidePose();
                    break;
                case StuntState.ButtSlide:
                    ComputeButtSlidePose();
                    break;
                case StuntState.Cannonball:
                    ComputeCannonballPose();
                    break;
                case StuntState.SideLying:
                    ComputeSideLyingPose();
                    break;
                case StuntState.DiveProne:
                    ComputeDivePronePose();
                    break;
                case StuntState.GroundRoll:
                    ComputeGroundRollPose();
                    break;
                case StuntState.WallRun:
                    ComputeWallRunPose();
                    break;
                case StuntState.Aerial:
                    ComputeAerialPose();
                    break;
                case StuntState.Kick:
                    ComputeKickPose();
                    break;
                case StuntState.GetUp:
                    ComputeGetUpPose();
                    break;
            }

            // Slew current poses toward target poses so state switches never step-shock the physics
            if (!_poseInitialized)
            {
                _curLeftHip = LeftHipTarget;
                _curRightHip = RightHipTarget;
                _curLeftKnee = LeftKneeTarget;
                _curRightKnee = RightKneeTarget;
                _poseInitialized = true;
            }
            else
            {
                float maxStep = MaxPoseSlewRate * Time.fixedDeltaTime;
                _curLeftHip = Quaternion.RotateTowards(_curLeftHip, LeftHipTarget, maxStep);
                _curRightHip = Quaternion.RotateTowards(_curRightHip, RightHipTarget, maxStep);
                _curLeftKnee = Quaternion.RotateTowards(_curLeftKnee, LeftKneeTarget, maxStep);
                _curRightKnee = Quaternion.RotateTowards(_curRightKnee, RightKneeTarget, maxStep);
            }

            ApplyPoseToJoints();
        }

        // ── POSE COMPUTATIONS ───────────────────────────────────────

        private void ComputeStandingPose()
        {
            LeftHipTarget = Quaternion.Euler(-5f, 0f, 0f);
            RightHipTarget = Quaternion.Euler(-5f, 0f, 0f);
            LeftKneeTarget = Quaternion.Euler(5f, 0f, 0f);
            RightKneeTarget = Quaternion.Euler(5f, 0f, 0f);
        }

        private void ComputeKneeSlidePose()
        {
            float kneeClearance = _mod.ObstacleClearance != null ? _mod.ObstacleClearance.GetKneeClearance() : 0f;
            LeftHipTarget = Quaternion.Euler(20f, 0f, 0f);
            RightHipTarget = Quaternion.Euler(20f, 0f, 0f);
            LeftKneeTarget = Quaternion.Euler(90f + kneeClearance, 0f, 0f);
            RightKneeTarget = Quaternion.Euler(90f + kneeClearance, 0f, 0f);
        }

        private void ComputeButtSlidePose()
        {
            LeftHipTarget = Quaternion.Euler(-60f, 0f, 0f);
            RightHipTarget = Quaternion.Euler(-60f, 0f, 0f);
            LeftKneeTarget = Quaternion.Euler(25f, 0f, 0f);
            RightKneeTarget = Quaternion.Euler(25f, 0f, 0f);
        }

        private void ComputeCannonballPose()
        {
            LeftHipTarget = Quaternion.Euler(90f, 0f, 0f);
            RightHipTarget = Quaternion.Euler(90f, 0f, 0f);
            LeftKneeTarget = Quaternion.Euler(110f, 0f, 0f);
            RightKneeTarget = Quaternion.Euler(110f, 0f, 0f);
        }

        private void ComputeSideLyingPose()
        {
            float spineRoll = GetSpineRoll();
            if (spineRoll > 35f) _preferRightDown = true;
            else if (spineRoll < -35f) _preferRightDown = false;

            float tuck = GetProneTuckBlend(2.0f);
            Quaternion tuckHip = Quaternion.Euler(30f, 0f, 0f);
            Quaternion tuckKnee = Quaternion.Euler(40f, 0f, 0f);

            if (_preferRightDown)
            {
                // Lying on right side: right leg = ground runner, left = sky bent
                RightHipTarget = Quaternion.Slerp(Quaternion.Euler(-5f, 0f, 0f), tuckHip, tuck);
                LeftHipTarget = Quaternion.Slerp(Quaternion.Euler(35f, 0f, 0f), tuckHip, tuck);
                RightKneeTarget = Quaternion.Slerp(Quaternion.Euler(5f, 0f, 0f), tuckKnee, tuck);
                LeftKneeTarget = Quaternion.Slerp(Quaternion.Euler(35f, 0f, 0f), tuckKnee, tuck);
            }
            else
            {
                // Lying on left side: left leg = ground runner, right = sky bent
                LeftHipTarget = Quaternion.Slerp(Quaternion.Euler(-5f, 0f, 0f), tuckHip, tuck);
                RightHipTarget = Quaternion.Slerp(Quaternion.Euler(35f, 0f, 0f), tuckHip, tuck);
                LeftKneeTarget = Quaternion.Slerp(Quaternion.Euler(5f, 0f, 0f), tuckKnee, tuck);
                RightKneeTarget = Quaternion.Slerp(Quaternion.Euler(35f, 0f, 0f), tuckKnee, tuck);
            }
        }

        private void ComputeDivePronePose()
        {
            bool liesOnBack = GetLiesOnBack();
            Quaternion baseHip = Quaternion.Euler(liesOnBack ? -10f : 8f, 0f, 0f);
            Quaternion baseKnee = Quaternion.Euler(liesOnBack ? 20f : 15f, 0f, 0f);

            // Subtle tuck only under fast physical yaw turn (never from stick deflection)
            float tuck = GetProneTuckBlend(2.0f);
            Quaternion tuckHip = Quaternion.Euler(liesOnBack ? -10f : 25f, 0f, 0f);
            Quaternion tuckKnee = Quaternion.Euler(liesOnBack ? 20f : 35f, 0f, 0f);

            LeftHipTarget = Quaternion.Slerp(baseHip, tuckHip, tuck);
            RightHipTarget = Quaternion.Slerp(baseHip, tuckHip, tuck);
            LeftKneeTarget = Quaternion.Slerp(baseKnee, tuckKnee, tuck);
            RightKneeTarget = Quaternion.Slerp(baseKnee, tuckKnee, tuck);
        }

        private void ComputeGroundRollPose()
        {
            LeftHipTarget = Quaternion.Euler(70f, 0f, 0f);
            RightHipTarget = Quaternion.Euler(70f, 0f, 0f);
            LeftKneeTarget = Quaternion.Euler(90f, 0f, 0f);
            RightKneeTarget = Quaternion.Euler(90f, 0f, 0f);
        }

        private void ComputeWallRunPose()
        {
            float stride = Mathf.Sin(Time.time * 12f) * 35f;

            if (_mod.StateMachine.WallIsRight)
            {
                RightHipTarget = Quaternion.Euler(stride, 30f, -25f);
                RightKneeTarget = Quaternion.Euler(70f + stride * 0.5f, 0f, 0f);
                LeftHipTarget = Quaternion.Euler(-stride * 0.5f, -10f, 0f);
                LeftKneeTarget = Quaternion.Euler(20f, 0f, 0f);
            }
            else
            {
                LeftHipTarget = Quaternion.Euler(stride, -30f, 25f);
                LeftKneeTarget = Quaternion.Euler(70f + stride * 0.5f, 0f, 0f);
                RightHipTarget = Quaternion.Euler(-stride * 0.5f, 10f, 0f);
                RightKneeTarget = Quaternion.Euler(20f, 0f, 0f);
            }
        }

        private void ComputeAerialPose()
        {
            LeftHipTarget = Quaternion.Euler(45f, 0f, 0f);
            RightHipTarget = Quaternion.Euler(45f, 0f, 0f);
            LeftKneeTarget = Quaternion.Euler(60f, 0f, 0f);
            RightKneeTarget = Quaternion.Euler(60f, 0f, 0f);
        }

        private void ComputeKickPose()
        {
            LeftHipTarget = Quaternion.Euler(-40f, 0f, 0f);
            RightHipTarget = Quaternion.Euler(40f, 0f, 0f);
            LeftKneeTarget = Quaternion.Euler(5f, 0f, 0f);
            RightKneeTarget = Quaternion.Euler(60f, 0f, 0f);
        }

        private void ComputeGetUpPose()
        {
            LeftHipTarget = Quaternion.Euler(-45f, 0f, 0f);
            RightHipTarget = Quaternion.Euler(-45f, 0f, 0f);
            LeftKneeTarget = Quaternion.Euler(85f, 0f, 0f);
            RightKneeTarget = Quaternion.Euler(85f, 0f, 0f);
        }

        // ── JOINT APPLICATION ───────────────────────────────────────

        private void ApplyPoseToJoints()
        {
            float bendSign = _mod.LegBendInvert ? -1f : 1f;

            var hips = _mod.JointControl.HipJoints;
            var knees = _mod.JointControl.KneeJoints;

            if (hips != null)
            {
                for (int i = 0; i < hips.Length; i++)
                {
                    var joint = hips[i];
                    if (joint == null) continue;
                    bool isLeft = IsLeftJoint(joint, i);
                    Quaternion target = isLeft ? _curLeftHip : _curRightHip;
                    joint.targetRotation = ComputeJointTargetRotation(joint, ApplyBendSign(target, bendSign));
                }
            }

            if (knees != null)
            {
                for (int i = 0; i < knees.Length; i++)
                {
                    var joint = knees[i];
                    if (joint == null) continue;
                    bool isLeft = IsLeftJoint(joint, i);
                    Quaternion target = isLeft ? _curLeftKnee : _curRightKnee;
                    joint.targetRotation = ComputeJointTargetRotation(joint, ApplyBendSign(target, bendSign));
                }
            }
        }

        private bool IsLeftJoint(ConfigurableJoint joint, int index)
        {
            string n = joint.gameObject.name.ToLower();
            if (n.Contains("left") || n.Contains("lf")) return true;
            if (n.Contains("right") || n.Contains("rt")) return false;
            if (n.EndsWith("l")) return true;
            if (n.EndsWith("r")) return false;
            return index == 0;
        }

        private Quaternion ApplyBendSign(Quaternion target, float bendSign)
        {
            if (bendSign >= 0f) return target;

            Vector3 euler = target.eulerAngles;
            euler.x *= bendSign;
            return Quaternion.Euler(euler);
        }

        /// <summary>
        /// Converts a desired local-space pose delta into the joint's internal coordinate frame.
        /// Purely local to the skeleton: 100% invariant to how the avatar rotates in the world.
        /// </summary>
        private Quaternion ComputeJointTargetRotation(ConfigurableJoint joint, Quaternion desiredPose)
        {
            if (joint == null) return Quaternion.identity;

            // Invert because PhysX drive acts from target to current
            Quaternion inv = Quaternion.Inverse(desiredPose);

            Vector3 right = joint.axis;
            Vector3 forward = Vector3.Cross(joint.axis, joint.secondaryAxis);
            if (forward.sqrMagnitude < 0.0001f)
            {
                return inv;
            }
            forward.Normalize();
            Vector3 up = Vector3.Cross(forward, right).normalized;
            Quaternion jointSpace = Quaternion.LookRotation(forward, up);

            return Quaternion.Inverse(jointSpace) * inv * jointSpace;
        }

        // ── HELPERS ──────────────────────────────────────────────────

        private float GetSpineRoll()
        {
            var rb = _mod.CachedSpineRb ?? _mod.CachedPelvisRb;
            if (rb == null) return 0f;
            Vector3 v = rb.transform.right;
            if (!PayneLabUtils.IsFinite(v)) return 0f;
            return Mathf.Asin(Mathf.Clamp(v.y, -1f, 1f)) * Mathf.Rad2Deg;
        }

        private bool GetLiesOnBack()
        {
            var rb = _mod.CachedSpineRb ?? _mod.CachedPelvisRb;
            if (rb == null) return false;
            Vector3 up = rb.transform.up;
            if (!PayneLabUtils.IsFinite(up)) return false;
            return Vector3.Dot(up, Vector3.up) > 0.3f;
        }

        private float GetProneTuckBlend(float settleRate)
        {
            var rb = _mod.CachedSpineRb ?? _mod.CachedPelvisRb;
            if (rb == null) return 0f;

            Vector3 angVel = rb.angularVelocity;
            if (!PayneLabUtils.IsFinite(angVel)) angVel = Vector3.zero;

            // Only actual rapid physical yawing tucks knees (Right Stick deflection is omitted
            // so aiming and turning will never cause the knees to pump like bicycle pedals).
            float yawRate = Mathf.Abs(angVel.y);
            float target = Mathf.Clamp01((yawRate - 1.5f) / 3.0f);

            float blend = _proneTuckBlend;
            float step = Time.fixedDeltaTime * settleRate;
            if (target > blend) _proneTuckBlend = Mathf.MoveTowards(blend, target, step * 1.5f);
            else _proneTuckBlend = Mathf.MoveTowards(blend, target, step);
            return _proneTuckBlend;
        }
    }
}