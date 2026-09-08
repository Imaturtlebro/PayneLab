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
        private float _diveTuckBlend;
        private float _smoothedDiveIntensity;

        public PayneLabLegPoses(PayneLabMod mod) { _mod = mod; }

        public void OnStateChanged(StuntState from, StuntState to)
        {
            // Reset dive-specific blending when leaving DiveProne state
            if (from == StuntState.DiveProne && to != StuntState.DiveProne)
            {
                _diveTuckBlend = 0f;
                _smoothedDiveIntensity = 0f;
            }
        }

        public void ResetPoseTargets()
        {
            _poseInitialized = false;
            _curLeftHip = Quaternion.identity;
            _curRightHip = Quaternion.identity;
            _curLeftKnee = Quaternion.identity;
            _curRightKnee = Quaternion.identity;
            _proneTuckBlend = 0f;
            _diveTuckBlend = 0f;
            _smoothedDiveIntensity = 0f;
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
            Vector3 velocity = GetPelvisVelocity();
            float horizontalSpeed = new Vector2(velocity.x, velocity.z).magnitude;
            float verticalSpeed = velocity.y;
            
            // Calculate raw dive intensity from velocity
            float rawDiveIntensity = 0f;
            
            // Horizontal speed contribution - faster dives = more extended legs
            float speedExtension = Mathf.Clamp01(horizontalSpeed / 8f);
            rawDiveIntensity = speedExtension;
            
            // Add extension when falling fast (diving downward)
            if (verticalSpeed < -2f)
                rawDiveIntensity = Mathf.Max(rawDiveIntensity, Mathf.Clamp01(Mathf.Abs(verticalSpeed) / 10f));
            
            // Smooth dive intensity to prevent jittery transitions
            float smoothStep = Time.fixedDeltaTime * 3f;
            _smoothedDiveIntensity = Mathf.MoveTowards(_smoothedDiveIntensity, rawDiveIntensity, smoothStep);
            float diveIntensity = _smoothedDiveIntensity;
            
            // Base pose: prone position with slight hip flexion
            float baseHipAngle = liesOnBack ? -10f : 8f;
            float baseKneeAngle = liesOnBack ? 20f : 15f;
            
            // Extended dive pose: legs trail behind with hip extension, knees straighter
            // More aggressive extension for face-down dives (classic superman pose)
            float hipExtensionRange = liesOnBack ? 10f : 35f;
            float kneeExtensionRange = liesOnBack ? 8f : 20f;
            float extendedHipAngle = liesOnBack ? -5f : 15f + (diveIntensity * hipExtensionRange);
            float extendedKneeAngle = liesOnBack ? 15f : 8f + (diveIntensity * kneeExtensionRange);
            
            // Interpolate base and extended poses based on dive intensity
            Quaternion baseHip = Quaternion.Euler(baseHipAngle, 0f, 0f);
            Quaternion baseKnee = Quaternion.Euler(baseKneeAngle, 0f, 0f);
            Quaternion extendedHip = Quaternion.Euler(extendedHipAngle, 0f, 0f);
            Quaternion extendedKnee = Quaternion.Euler(extendedKneeAngle, 0f, 0f);
            
            Quaternion dynamicHip = Quaternion.Slerp(baseHip, extendedHip, diveIntensity);
            Quaternion dynamicKnee = Quaternion.Slerp(baseKnee, extendedKnee, diveIntensity);
            
            // Directional asymmetry: weight shift toward dive direction
            Vector2 ls = _mod.Input.LeftStick;
            float lateralShift = 0f;
            if (ls.sqrMagnitude > 0.1f && horizontalSpeed > 1f)
            {
                // Determine if diving left or right relative to facing direction
                Vector3 headFwd = GetFacingDirection();
                Vector3 headRight = Vector3.Cross(Vector3.up, headFwd).normalized;
                float lateralInput = Vector3.Dot(new Vector3(ls.x, 0f, ls.y).normalized, headRight);
                lateralShift = Mathf.Clamp(lateralInput * diveIntensity * 0.6f, -0.6f, 0.6f);
            }
            
            // Apply asymmetric hip abduction/adduction for directional dives
            float leftHipRoll = lateralShift * 15f;
            float rightHipRoll = -lateralShift * 15f;
            
            // Dynamic tuck on rapid rotation (yaw, pitch, or roll) - separate blend for dive state
            float tuck = GetDiveTuckBlend();
            Quaternion tuckHip = Quaternion.Euler(liesOnBack ? -10f : 25f, 0f, 0f);
            Quaternion tuckKnee = Quaternion.Euler(liesOnBack ? 20f : 35f, 0f, 0f);
            
            // Blend between extended dive pose and tucked pose
            LeftHipTarget = Quaternion.Slerp(
                Quaternion.Euler(dynamicHip.eulerAngles.x, 0f, leftHipRoll),
                tuckHip,
                tuck
            );
            RightHipTarget = Quaternion.Slerp(
                Quaternion.Euler(dynamicHip.eulerAngles.x, 0f, rightHipRoll),
                tuckHip,
                tuck
            );
            LeftKneeTarget = Quaternion.Slerp(dynamicKnee, tuckKnee, tuck);
            RightKneeTarget = Quaternion.Slerp(dynamicKnee, tuckKnee, tuck);
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
            // Animate feet/legs through a natural standing-up motion
            // Transition blends from prone/supine position to standing over ~0.4s
            
            // Get progress from state machine's get-up timer (0 = just started, 1 = complete)
            float getUpProgress = _mod.StateMachine != null ? _mod.StateMachine.GetUpProgress : 1f;
            
            // Start pose: legs extended forward (prone/supine resting position)
            Quaternion startHip = Quaternion.Euler(-30f, 0f, 0f);
            Quaternion startKnee = Quaternion.Euler(10f, 0f, 0f);
            
            // Mid pose: knees tuck under body as hips rise (~40% through)
            Quaternion midHip = Quaternion.Euler(-65f, 0f, 0f);
            Quaternion midKnee = Quaternion.Euler(100f, 0f, 0f);
            
            // End pose: standing position
            Quaternion endHip = Quaternion.Euler(-5f, 0f, 0f);
            Quaternion endKnee = Quaternion.Euler(5f, 0f, 0f);
            
            // Use smoothstep for natural acceleration/deceleration
            float smoothProgress = getUpProgress * getUpProgress * (3f - 2f * getUpProgress);
            
            // Two-phase animation: first tuck knees under (0-0.45), then extend to stand (0.45-1.0)
            Quaternion targetHip, targetKnee;
            float transitionPoint = 0.45f;
            
            if (smoothProgress < transitionPoint)
            {
                // Phase 1: Tuck knees under the body
                float phase1 = smoothProgress / transitionPoint;
                targetHip = Quaternion.Slerp(startHip, midHip, phase1);
                targetKnee = Quaternion.Slerp(startKnee, midKnee, phase1);
            }
            else
            {
                // Phase 2: Extend legs to standing position
                float phase2 = (smoothProgress - transitionPoint) / (1f - transitionPoint);
                targetHip = Quaternion.Slerp(midHip, endHip, phase2);
                targetKnee = Quaternion.Slerp(midKnee, endKnee, phase2);
            }
            
            LeftHipTarget = targetHip;
            RightHipTarget = targetHip;
            LeftKneeTarget = targetKnee;
            RightKneeTarget = targetKnee;
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

        /// <summary>
        /// Enhanced dynamic tuck detection that responds to pitch, roll, AND yaw rotation rates.
        /// This creates natural knee tucking during aerial flips, barrel rolls, and rapid turns.
        /// </summary>
        private float GetDynamicTuckBlend()
        {
            var rb = _mod.CachedSpineRb ?? _mod.CachedPelvisRb;
            if (rb == null) return 0f;

            Vector3 angVel = rb.angularVelocity;
            if (!PayneLabUtils.IsFinite(angVel)) angVel = Vector3.zero;

            // Total angular speed across all axes (pitch, yaw, roll)
            float totalAngSpeed = angVel.magnitude;
            
            // Tuck threshold: start tucking at ~2 rad/s (~115 deg/s), full tuck at ~5 rad/s
            float target = Mathf.Clamp01((totalAngSpeed - 2.0f) / 3.0f);

            float blend = _proneTuckBlend;
            float step = Time.fixedDeltaTime * 2.5f;
            if (target > blend) _proneTuckBlend = Mathf.MoveTowards(blend, target, step * 1.5f);
            else _proneTuckBlend = Mathf.MoveTowards(blend, target, step);
            return _proneTuckBlend;
        }

        /// <summary>
        /// Specialized tuck detection for dive/prone state with faster response and lower thresholds.
        /// Diving requires quicker knee tuck reactions for aerial maneuvers.
        /// </summary>
        private float GetDiveTuckBlend()
        {
            var rb = _mod.CachedSpineRb ?? _mod.CachedPelvisRb;
            if (rb == null) return 0f;

            Vector3 angVel = rb.angularVelocity;
            if (!PayneLabUtils.IsFinite(angVel)) angVel = Vector3.zero;

            // Total angular speed across all axes (pitch, yaw, roll)
            float totalAngSpeed = angVel.magnitude;
            
            // Lower threshold for diving: start tucking at ~1.2 rad/s (~70 deg/s), full tuck at ~3.5 rad/s
            // Faster response for aerial diving maneuvers
            float target = Mathf.Clamp01((totalAngSpeed - 1.2f) / 2.3f);

            float blend = _diveTuckBlend;
            float step = Time.fixedDeltaTime * 4f;
            if (target > blend) _diveTuckBlend = Mathf.MoveTowards(blend, target, step * 2f);
            else _diveTuckBlend = Mathf.MoveTowards(blend, target, step);
            return _diveTuckBlend;
        }

        private Vector3 GetPelvisVelocity()
        {
            var rb = _mod.CachedPelvisRb ?? _mod.CachedSpineRb;
            if (rb == null) return Vector3.zero;
            Vector3 v = rb.velocity;
            return PayneLabUtils.IsFinite(v) ? v : Vector3.zero;
        }

        private Vector3 GetFacingDirection()
        {
            var rb = _mod.CachedSpineRb ?? _mod.CachedPelvisRb;
            if (rb == null) return Vector3.forward;
            Vector3 fwd = rb.transform.forward;
            return PayneLabUtils.IsFinite(fwd) ? fwd.normalized : Vector3.forward;
        }
    }
}
