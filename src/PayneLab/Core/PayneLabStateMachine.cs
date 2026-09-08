using BoneLib;
using UnityEngine;
using Il2CppSLZ.Marrow;

namespace PayneLab
{
    public class PayneLabStateMachine
    {
        private readonly PayneLabMod _mod;

        private float _actionLockTimer = 0f;
        private bool _isGettingUp = false;
        private float _getUpTimer = 0f;
        private bool _kickActive;
        private float _kickTimer;
        private const float KickDuration = 0.35f;
        private const float GetUpPlantDuration = 0.15f;
        private const float GetUpTotalDuration = 0.40f;
        private Vector3 _entryVelocity;

        // Grounded bookkeeping (slide/stand/gate control)
        private float _groundedTime;
        private float _slideStayTimer;
        private float _orientationDwell;
        private float _lastAutoSlideTime = -10f;
        private const float AutoSlideCooldown = 1.2f;
        private const float SlideStandSpeed = 1.2f;
        private const float SlidePitchThreshold = -20f;
        private const float GetUpGroundTimeRequired = 0.25f;
        private const float GetUpVerticalAssist = 16f;
        private const float GetUpMaxUpwardVelocity = 1.2f;

        // Robust Prone <-> SideLying classifier guards
        private float _lastProneStateChangeTime = -10f;
        private const float ProneStateCooldown = 0.50f;
        private const float EnterSideLyingRollAngle = 60f;
        private const float ExitSideLyingRollAngle = 30f;
        private const float MaxProneClassifyAngularSpeed = 1.8f; // ~100 deg/s

        // Wallrun state
        private bool _isWallRunning;
        private Vector3 _wallNormal = Vector3.zero;
        private bool _isWallOnRight;
        private float _wallRunTimer;
        private float _wallReleaseGraceTimer;
        private int _wallContactLostFrames;
        private const int WallContactLostGraceFrames = 5;
        private const float WallReleaseGraceWindow = 0.20f;
        private const float WallRunAntiGravityAccel = 9.81f;
        private const float WallRunStickForce = 15f;
        private const float WallRunEntryLift = 2.2f;
        private const float WallRunMinTangentSpeed = 2.0f;
        private const float WallJumpForwardScale = 0.6f;
        private const float WallJumpUpScale = 0.3f;
        private const float WallJumpWallScale = 0.1f;

        private int _airborneFrames;
        private const int AirborneConfirmFrames = 7;
        private float _lastAirAutoStateTime = -10f;
        private const float AirStateCooldown = 0.20f;
        private const float GroundProbeSpan = 3f;

        private float _groundedVelTimer;
        private const float GroundedVFactor = 0.20f; // 200ms stable vy prevents apex false-positives
        private const float GroundedUpBound = 1.5f;
        private const float GroundedDownBound = -1.0f;

        private bool _contactGrounded;
        private bool _contactWall;
        private Vector3 _contactWallNormal = Vector3.zero;

        internal void OnContactsUpdate(bool grounded, bool wall, Vector3 wallNormal)
        {
            _contactGrounded = grounded;
            _contactWall = wall;
            _contactWallNormal = wallNormal;
        }

        public bool IsWallRunning => _isWallRunning;
        public Vector3 WallNormal => _wallNormal;
        public bool WallIsRight => _isWallOnRight;
        public int AirborneFrames => _airborneFrames;
        public float ActionLockTimer => _actionLockTimer;
        public float WallRunTimerValue => _wallRunTimer;
        public float WallReleaseGraceTimerValue => _wallReleaseGraceTimer;
        public bool ContactGrounded => _contactGrounded;

        // Diagnostic accessors (used by the state-transition audit trail).
        public float TorsoPitch
        {
            get
            {
                var rb = _mod.CachedSpineRb ?? _mod.CachedPelvisRb;
                if (rb == null) return 0f;
                return Mathf.Asin(Mathf.Clamp(rb.transform.forward.y, -1f, 1f)) * Mathf.Rad2Deg;
            }
        }
        public float SpineRoll
        {
            get
            {
                var rb = _mod.CachedSpineRb ?? _mod.CachedPelvisRb;
                if (rb == null) return 0f;
                Vector3 v = rb.transform.right;
                if (!PayneLabUtils.IsFinite(v)) return 0f;
                return Mathf.Asin(Mathf.Clamp(v.y, -1f, 1f)) * Mathf.Rad2Deg;
            }
        }
        public float PelvisVy
        {
            get
            {
                var rb = _mod.CachedPelvisRb;
                if (rb == null) return 0f;
                Vector3 v = rb.velocity;
                return PayneLabUtils.IsFinite(v) ? v.y : 0f;
            }
        }
        
        // Expose get-up timer for leg pose animation blending
        public float GetUpProgress
        {
            get
            {
                if (!_isGettingUp) return 1f;
                // Normalize get-up timer to 0-1 range over the total duration
                return Mathf.Clamp01(_getUpTimer / GetUpTotalDuration);
            }
        }

        public PayneLabStateMachine(PayneLabMod mod) { _mod = mod; }

        public void Tick()
        {
            var rig = _mod.CachedRig;
            if (rig == null) return;

            if (IsGrounded()) _groundedTime += Time.fixedDeltaTime;
            else _groundedTime = 0f;

            if (_actionLockTimer > 0f) _actionLockTimer = Mathf.Max(0f, _actionLockTimer - Time.fixedDeltaTime);

            // Manual get-up sequence in progress
            if (_isGettingUp)
            {
                TickGetUp();
                return;
            }

            // Wallrun state tick
            if (_mod.CurrentState == StuntState.WallRun)
            {
                TickWallRun();
                return;
            }

            // Release grace buffer
            if (_wallReleaseGraceTimer > 0f)
            {
                _wallReleaseGraceTimer -= Time.fixedDeltaTime;
                if (_mod.Input.JumpPressed)
                {
                    ExecuteWallJump();
                    return;
                }
            }

            // Mode switch
            if (_mod.Input.WantModeSwitch)
            {
                CycleInputMode();
            }

            // Dual-stick click: get-up if grounded/limp, otherwise toggle limp
            if (_mod.Input.WantRagdollToggle)
            {
                if ((IsGroundedState(_mod.CurrentState) || _mod.ActiveInput == InputMode.FullRagdoll) &&
                    IsGrounded())
                {
                    StartGetUpSequence();
                    return;
                }

                ToggleFullRagdoll();
                return;
            }

            // Get-up fallback (RS click while down)
            if (_mod.Input.WantGetUp &&
                (IsGroundedState(_mod.CurrentState) || _mod.ActiveInput == InputMode.FullRagdoll) &&
                IsGrounded())
            {
                StartGetUpSequence();
                return;
            }

            // Kick action
            if (_mod.Input.WantKick && _mod.CurrentState == StuntState.Standing)
            {
                ExecuteKick();
                return;
            }

            // Dive action
            if (_mod.Input.WantDive && _mod.CurrentState == StuntState.Standing)
            {
                ExecuteDive();
                return;
            }

            // Slide-jump slingshot
            if (_mod.Input.JumpPressed &&
                (_mod.CurrentState == StuntState.KneeSlide || _mod.CurrentState == StuntState.ButtSlide))
            {
                ExecuteSlideJump();
                return;
            }

            if (_actionLockTimer > 0f || _kickActive) return;

            DetectMovementState();
        }

        // === STATE DETECTION ===

        private void DetectMovementState()
        {
            if (_mod.ActiveInput == InputMode.FullRagdoll)
            {
                if (_mod.CurrentState != StuntState.DiveProne && _mod.CurrentState != StuntState.SideLying)
                    _mod.SetState(StuntState.DiveProne);
                return;
            }

            Vector3 velocity = GetVelocity();
            float horizontalSpeed = new Vector2(velocity.x, velocity.z).magnitude;
            float torsoPitch = GetTorsoPitch();
            bool isGrounded = IsGrounded();

            if (isGrounded) _airborneFrames = 0;
            else _airborneFrames++;

            // Wallrun entry
            if (!isGrounded &&
                _mod.Input.WallrunIntentHeld &&
                TryDetectWall(out Vector3 wallNormal, out bool wallRight))
            {
                EnterWallRun(wallNormal, wallRight);
                return;
            }

            // Air check with hysteresis
            if (!isGrounded && _airborneFrames >= AirborneConfirmFrames && !IsGroundedState(_mod.CurrentState))
            {
                float spineUpDot = GetSpineUpDot();
                bool physicallyInverted = _mod.CachedHeadRb != null && _mod.CachedPelvisRb != null &&
                    _mod.CachedHeadRb.position.y < _mod.CachedPelvisRb.position.y - 0.25f;

                StuntState target;
                if (physicallyInverted && spineUpDot < 0.05f &&
                    _mod.CurrentState == StuntState.Aerial)
                    target = StuntState.Cannonball;
                else if (_mod.CurrentState == StuntState.Cannonball && spineUpDot < 0.05f)
                    target = StuntState.Cannonball;
                else
                    target = StuntState.Aerial;

                if (target == StuntState.Cannonball && _mod.CurrentState != StuntState.Cannonball)
                {
                    float now = Time.fixedTime;
                    if (now - _lastAirAutoStateTime < AirStateCooldown) return;
                    _lastAirAutoStateTime = now;
                }

                if (_mod.CurrentState != target) _mod.SetState(target);
                return;
            }

            if (isGrounded)
            {
                HandleGrounded(horizontalSpeed, torsoPitch);
                return;
            }

            if (!IsGroundedState(_mod.CurrentState) && _mod.CurrentState != StuntState.Standing &&
                _mod.CurrentState != StuntState.Kick && _mod.CurrentState != StuntState.WallRun)
            {
                _mod.SetState(StuntState.Aerial);
            }
        }

        private void HandleGrounded(float horizontalSpeed, float torsoPitch)
        {
            if (_actionLockTimer > 0f) return;

            if (_mod.Input.WantGroundRoll)
            {
                _mod.SetState(StuntState.GroundRoll);
                _actionLockTimer = 0.45f;
                return;
            }

            switch (_mod.CurrentState)
            {
                case StuntState.Standing:
                    if (horizontalSpeed > _mod.KneeSlideSpeedThreshold && torsoPitch < SlidePitchThreshold)
                    {
                        float now = Time.fixedTime;
                        if (now - _lastAutoSlideTime >= AutoSlideCooldown)
                        {
                            _lastAutoSlideTime = now;
                            _slideStayTimer = 0.8f;
                            _mod.SetState(torsoPitch < -45f ? StuntState.KneeSlide : StuntState.ButtSlide);
                        }
                    }
                    return;

                case StuntState.KneeSlide:
                case StuntState.ButtSlide:
                    _slideStayTimer -= Time.fixedDeltaTime;
                    if (horizontalSpeed < SlideStandSpeed && _slideStayTimer <= 0f)
                    {
                        _slideStayTimer = 0.8f;
                        _mod.SetState(StuntState.Standing);
                    }
                    return;

                case StuntState.DiveProne:
                case StuntState.SideLying:
                    float spineRoll = Mathf.Abs(GetSpineRoll());
                    float nowTime = Time.fixedTime;

                    // 1. Cooldown gate: prevent fast limit-cycle toggling
                    if (nowTime - _lastProneStateChangeTime < ProneStateCooldown)
                    {
                        _orientationDwell = 0f;
                        return;
                    }

                    // 2. Spin gate: do not classify while the body is actively tumbling/rolling
                    var spineRb = _mod.CachedSpineRb ?? _mod.CachedPelvisRb;
                    if (spineRb != null && spineRb.angularVelocity.magnitude > MaxProneClassifyAngularSpeed)
                    {
                        _orientationDwell = 0f;
                        return;
                    }

                    // 3. Asymmetric hysteresis thresholds (60 deg in, 30 deg out)
                    StuntState betterState = _mod.CurrentState;
                    if (_mod.CurrentState == StuntState.DiveProne)
                    {
                        if (spineRoll > EnterSideLyingRollAngle)
                            betterState = StuntState.SideLying;
                    }
                    else // SideLying
                    {
                        if (spineRoll < ExitSideLyingRollAngle)
                            betterState = StuntState.DiveProne;
                    }

                    if (betterState == _mod.CurrentState)
                    {
                        _orientationDwell = 0f;
                        return;
                    }

                    // 4. Stable dwell requirement (0.35s)
                    _orientationDwell += Time.fixedDeltaTime;
                    if (_orientationDwell >= 0.35f)
                    {
                        _orientationDwell = 0f;
                        _lastProneStateChangeTime = nowTime;
                        _mod.SetState(betterState);
                    }
                    return;

                case StuntState.GroundRoll:
                case StuntState.GetUp:
                    return;

                default:
                    _mod.SetState(StuntState.Standing);
                    return;
            }
        }

        // === MANUAL GET-UP SEQUENCE ===

        public void StartGetUpSequence()
        {
            _isGettingUp = true;
            _getUpTimer = 0f;
            _airborneFrames = 0;
            _groundedVelTimer = 1.0f;
            _groundedTime = 0.5f;

            _mod.ActiveInput = InputMode.ProcLeg;
            _mod.SetState(StuntState.GetUp);
            _mod.LoggerInstance.Msg("[PayneLab] Initiating procedural get-up...");
        }

        private void TickGetUp()
        {
            _getUpTimer += Time.fixedDeltaTime;

            // Maintain grounded confirmation while rising so no stale counter triggers Aerial
            _airborneFrames = 0;
            _groundedVelTimer = 1.0f;

            var rig = _mod.CachedRig;
            if (rig == null) return;

            if (_getUpTimer < GetUpPlantDuration)
            {
                // plant phase
            }
            else if (_getUpTimer < GetUpTotalDuration)
            {
                if (_mod.CachedPelvisRb != null)
                {
                    _mod.CachedPelvisRb.AddForce(Vector3.up * GetUpVerticalAssist, ForceMode.Acceleration);

                    Vector3 v = _mod.CachedPelvisRb.velocity;
                    if (v.y > GetUpMaxUpwardVelocity)
                        _mod.CachedPelvisRb.velocity = new Vector3(v.x, GetUpMaxUpwardVelocity, v.z);
                }
            }
            else
            {
                _isGettingUp = false;
                _getUpTimer = 0f;

                // Reset all grounded/airborne bookkeeping so Standing never flickers into Aerial
                _airborneFrames = 0;
                _groundedVelTimer = 1.0f;
                _groundedTime = 0.5f;
                _actionLockTimer = 0.25f;

                rig.TurnOnRig();
                rig.UnRagdollRig();
                rig.EnableBallLoco();

                _mod.ActiveInput = InputMode.ProcLeg;
                _mod.SetState(StuntState.Standing);
                _mod.LoggerInstance.Msg("[PayneLab] Recovered to Standing.");
            }
        }

        public bool IsGroundedState(StuntState state)
        {
            return state == StuntState.KneeSlide ||
                   state == StuntState.ButtSlide ||
                   state == StuntState.DiveProne ||
                   state == StuntState.SideLying ||
                   state == StuntState.GroundRoll ||
                   state == StuntState.GetUp;
        }

        // === ACTIONS ===

        private void CaptureEntryMomentum()
        {
            var rb = _mod.CachedPelvisRb;
            _entryVelocity = (rb != null && PayneLabUtils.IsFinite(rb.velocity)) ? rb.velocity : Vector3.zero;
        }

        private void ApplyEntryMomentum()
        {
            var rb = _mod.CachedPelvisRb;
            if (rb == null || !PayneLabUtils.IsFinite(rb.velocity) || !PayneLabUtils.IsFinite(_entryVelocity)) return;
            if (_entryVelocity.sqrMagnitude < 0.001f) return;

            Vector3 target = new Vector3(_entryVelocity.x, 0f, _entryVelocity.z);
            Vector3 current = new Vector3(rb.velocity.x, 0f, rb.velocity.z);
            rb.AddForce(target - current, ForceMode.VelocityChange);
        }

        // Mass-normalizes impulse magnitudes so athletic maneuvers feel identical
        // across BONELAB's avatar range (~25kg Fast up to 130kg+ Heavy). Ratio is
        // computed from the pelvis+spine mass (approx. the driving core of an ~80kg
        // Ford avatar in this rig), clamped so extreme avatars can't over/undershoot.
        private float GetMassRatio()
        {
            float coreMass = 0f;
            if (_mod.CachedPelvisRb != null) coreMass += _mod.CachedPelvisRb.mass;
            if (_mod.CachedSpineRb != null) coreMass += _mod.CachedSpineRb.mass;
            if (coreMass <= 0f || !float.IsFinite(coreMass)) return 1f;
            return Mathf.Clamp(coreMass / 45f, 0.5f, 2.5f);
        }

        private void ToggleFullRagdoll()
        {
            var rig = _mod.CachedRig;
            if (rig == null) return;

            CaptureEntryMomentum();
            rig.PhysicalLegs();
            rig.DisableBallLoco();
            ApplyEntryMomentum();
            SetRigKinematic(rig, false);

            _mod.ActiveInput = InputMode.FullRagdoll;
            _mod.SetState(StuntState.DiveProne);
            _mod.LoggerInstance.Msg("[PayneLab] Manually Went Limp");
        }

        private void CycleInputMode()
        {
            switch (_mod.ActiveInput)
            {
                case InputMode.ProcLeg:
                    _mod.ActiveInput = InputMode.FullIK;
                    break;
                case InputMode.FullIK:
                    _mod.ActiveInput = InputMode.FullRagdoll;
                    break;
                case InputMode.FullRagdoll:
                    _mod.ActiveInput = InputMode.ProcLeg;
                    break;
            }
            _mod.LoggerInstance.Msg($"[PayneLab] Input Mode: {_mod.ActiveInput}");
        }

        private void ExecuteKick()
        {
            var rig = _mod.CachedRig;
            if (rig == null) return;

            rig.PhysicalLegs();
            rig.DisableBallLoco();
            _mod.SetState(StuntState.Kick);

            Vector3 kickDir = GetFacingDirection();
            float m = GetMassRatio();
            if (_mod.CachedFootRb != null)
                _mod.CachedFootRb.AddForce((kickDir * 320f + Vector3.up * 80f) * m, ForceMode.Impulse);
            if (_mod.CachedKneeRb != null)
                _mod.CachedKneeRb.AddForce((kickDir * 200f + Vector3.up * 40f) * m, ForceMode.Impulse);
            if (_mod.CachedPelvisRb != null)
                _mod.CachedPelvisRb.AddForce(kickDir * 160f * m, ForceMode.Impulse);

            _kickActive = true;
            _kickTimer = 0f;
            _actionLockTimer = KickDuration;
            _mod.LoggerInstance.Msg("[PayneLab] Kick Executed");
        }

        public void TickKick()
        {
            if (!_kickActive) return;

            _kickTimer += Time.fixedDeltaTime;
            if (_kickTimer >= KickDuration)
            {
                EndKick();
            }
        }

        private void EndKick()
        {
            _kickActive = false;
            _kickTimer = 0f;

            var rig = _mod.CachedRig;
            if (rig == null) return;

            rig.TurnOnRig();
            rig.UnRagdollRig();
            rig.EnableBallLoco();
            _mod.SetState(StuntState.Standing);
        }

        private void ExecuteDive()
        {
            var rig = _mod.CachedRig;
            if (rig == null) return;

            CaptureEntryMomentum();
            rig.PhysicalLegs();
            rig.DisableBallLoco();
            ApplyEntryMomentum();

            Vector3 headFwd = GetFacingDirection();
            Vector3 headRight = Vector3.Cross(Vector3.up, headFwd).normalized;
            Vector2 ls = _mod.Input.LeftStick;

            Vector3 horizontalDir = headFwd;
            if (ls.sqrMagnitude > 0.1f)
            {
                horizontalDir = (headFwd * ls.y + headRight * ls.x).normalized;
            }

            float m = GetMassRatio();
            Vector3 diveImpulse = (horizontalDir * 1.0f + Vector3.up * 0.20f).normalized * (_mod.DiveImpulseForce * 25f) * m;
            if (!PayneLabUtils.IsFinite(diveImpulse)) diveImpulse = Vector3.forward * (_mod.DiveImpulseForce * 25f) * m;

            if (_mod.CachedPelvisRb != null)
            {
                _mod.CachedPelvisRb.AddForce(diveImpulse, ForceMode.Impulse);
            }

            _mod.ActiveInput = InputMode.ProcLeg;
            _mod.SetState(StuntState.DiveProne);
            _actionLockTimer = 0.6f;
            _mod.LoggerInstance.Msg($"[PayneLab] Directional Dive: dir=({horizontalDir.x:0.00},{horizontalDir.z:0.00})");
        }

        private void ExecuteSlideJump()
        {
            var rb = _mod.CachedPelvisRb;
            if (rb == null) return;

            Vector3 fwd = GetFacingDirection();
            rb.AddForce(fwd * 4.0f + Vector3.up * 6.0f, ForceMode.VelocityChange);

            _mod.ActiveInput = InputMode.ProcLeg;
            _mod.SetState(StuntState.Aerial);
            _actionLockTimer = 0.3f;
            _mod.LoggerInstance.Msg("[PayneLab] Slide Jump Slingshot");
        }

        // ── WALLRUN & WALL JUMP ────────────────────────────────────

        private void EnterWallRun(Vector3 normal, bool onRight)
        {
            _isWallRunning = true;
            _wallNormal = normal;
            _isWallOnRight = onRight;
            _wallRunTimer = 0f;
            _actionLockTimer = 0f;
            _wallContactLostFrames = 0;

            if (_mod.CachedPelvisRb != null)
            {
                Vector3 v = _mod.CachedPelvisRb.velocity;
                if (v.y < 0f) v.y = 0f;
                _mod.CachedPelvisRb.velocity = v;
                _mod.CachedPelvisRb.AddForce(Vector3.up * WallRunEntryLift, ForceMode.VelocityChange);
            }

            _mod.ObstacleClearance.SetSlideFriction(0f);
            _mod.SetState(StuntState.WallRun);
            _mod.LoggerInstance.Msg("[PayneLab] Wallrun engaged");
        }

        private void TickWallRun()
        {
            var rb = _mod.CachedPelvisRb;
            if (rb == null)
            {
                ExitWallRun(false);
                return;
            }

            _wallRunTimer += Time.fixedDeltaTime;

            if (!TryDetectWall(out Vector3 normal, out bool wallRight))
            {
                _wallContactLostFrames++;
                if (_wallContactLostFrames < WallContactLostGraceFrames) return;
                ExitWallRun(false);
                return;
            }
            _wallContactLostFrames = 0;
            _wallNormal = normal;
            _isWallOnRight = wallRight;

            if (_contactGrounded || (IsGrounded() && GetGroundDistance() <= 1.0f))
            {
                ExitWallRun(false);
                return;
            }

            if (_mod.Input.JumpPressed)
            {
                ExecuteWallJump();
                return;
            }

            if (!_mod.Input.WallrunIntentHeld)
            {
                ExitWallRun(false);
                return;
            }

            if (_wallRunTimer >= _mod.WallrunMaxDuration)
            {
                ExitWallRun(true);
                return;
            }

            Vector3 tangent = Vector3.Cross(_wallNormal, Vector3.up);
            if (tangent.sqrMagnitude < 0.0001f ||
                !PayneLabUtils.IsFinite(tangent) || !PayneLabUtils.IsFinite(rb.velocity))
            {
                ExitWallRun(false);
                return;
            }
            tangent.Normalize();
            Vector3 horizontal = new Vector3(rb.velocity.x, 0f, rb.velocity.z);
            if (Vector3.Dot(tangent, horizontal) < 0f) tangent = -tangent;

            float tangentialSpeed = Mathf.Abs(Vector3.Dot(horizontal, tangent));
            Vector3 snapped = tangent * Mathf.Max(tangentialSpeed, WallRunMinTangentSpeed);
            rb.AddForce(snapped - horizontal, ForceMode.VelocityChange);

            rb.AddForce(Vector3.up * (WallRunAntiGravityAccel * (1f - _mod.WallrunGravityScale)), ForceMode.Acceleration);
            rb.AddForce(-_wallNormal * WallRunStickForce, ForceMode.Acceleration);
        }

        private void ExitWallRun(bool startGrace)
        {
            _isWallRunning = false;
            _wallReleaseGraceTimer = startGrace ? WallReleaseGraceWindow : 0f;
            _wallContactLostFrames = 0;

            _mod.ObstacleClearance.RestoreFriction();

            if (_mod.CurrentState == StuntState.WallRun)
            {
                _mod.SetState(StuntState.Aerial);
            }
        }

        private void ExecuteWallJump()
        {
            _isWallRunning = false;
            _wallReleaseGraceTimer = 0f;

            _mod.ObstacleClearance.RestoreFriction();

            Vector3 tangent = Vector3.Cross(_wallNormal, Vector3.up);
            if (tangent.sqrMagnitude < 0.0001f || !PayneLabUtils.IsFinite(tangent))
                tangent = GetFacingDirection();
            else
                tangent.Normalize();

            var rb = _mod.CachedPelvisRb;
            if (rb != null && PayneLabUtils.IsFinite(rb.velocity))
            {
                Vector3 horizontal = new Vector3(rb.velocity.x, 0f, rb.velocity.z);
                if (Vector3.Dot(tangent, horizontal) < 0f) tangent = -tangent;

                Vector3 jumpDir = (tangent * WallJumpForwardScale * _mod.WallJumpForce)
                                + (Vector3.up * WallJumpUpScale * _mod.WallJumpForce)
                                + (_wallNormal * WallJumpWallScale * _mod.WallJumpForce);
                rb.AddForce(jumpDir, ForceMode.VelocityChange);
            }

            _mod.ActiveInput = InputMode.ProcLeg;
            _mod.SetState(StuntState.Aerial);
            _actionLockTimer = 0.3f;
            _mod.LoggerInstance.Msg("[PayneLab] Wall Jump");
        }

        private bool TryDetectWall(out Vector3 normal, out bool onRight)
        {
            normal = Vector3.zero;
            onRight = false;

            if (_contactWall && _contactWallNormal.sqrMagnitude > 0.01f)
            {
                normal = _contactWallNormal.normalized;
                Vector3 sideAxis = _mod.CachedPelvisRb != null ? _mod.CachedPelvisRb.transform.right : GetFacingDirection();
                onRight = Vector3.Dot(normal, sideAxis) < 0f;
                return true;
            }

            var rb = _mod.CachedPelvisRb;
            if (rb == null) return false;

            Vector3 origin = rb.position;
            Vector3 right = rb.transform.right;
            Vector3 fwd = GetFacingDirection();
            float probe = _mod.WallProbeDistance;

            for (int side = 0; side < 2; side++)
            {
                Vector3 dir = side == 0 ? right : -right;
                for (int i = 0; i < 3; i++)
                {
                    Vector3 center = origin + dir * (0.35f + 0.22f * i) + fwd * (i - 1) * 0.15f;
                    if (PayneLabUtils.OverlapNormal(_mod, center, 0.3f, out Vector3 n) &&
                        Mathf.Abs(n.y) < 0.4f)
                    {
                        normal = n;
                        onRight = side == 0;
                        return true;
                    }
                }
            }

            if (PayneLabUtils.RaycastNonSelf(_mod, origin, right, out RaycastHit rightHit, probe) &&
                Mathf.Abs(rightHit.normal.y) < 0.25f)
            {
                normal = rightHit.normal;
                onRight = true;
                return true;
            }

            if (PayneLabUtils.RaycastNonSelf(_mod, origin, -right, out RaycastHit leftHit, probe) &&
                Mathf.Abs(leftHit.normal.y) < 0.25f)
            {
                normal = leftHit.normal;
                onRight = false;
                return true;
            }

            return false;
        }

        // === PHYSICS QUERIES ===

        public bool IsGrounded()
        {
            if (_contactGrounded) return true;

            Vector3 vel = GetVelocity();

            if (vel.y > GroundedUpBound || vel.y < GroundedDownBound)
            {
                _groundedVelTimer = 0f;
                return false;
            }

            _groundedVelTimer += Time.fixedDeltaTime;
            return _groundedVelTimer >= GroundedVFactor;
        }

        internal float GetGroundDistance()
        {
            if (_mod.CachedRig == null || _mod.CachedRig.m_pelvis == null) return 999f;

            Vector3 pelvisPos = _mod.CachedRig.m_pelvis.position;

            Vector3 top = pelvisPos + Vector3.up * 0.55f;
            Vector3 bottom = pelvisPos + Vector3.down * 0.95f;
            if (PayneLabUtils.CapsuleCastNonSelf(_mod, top, bottom, 0.22f, Vector3.down,
                    _mod.GroundRayDistance + 1.0f, out RaycastHit hit))
                return pelvisPos.y - hit.point.y;

            Vector3 origin = pelvisPos + Vector3.up * GroundProbeSpan;
            if (PayneLabUtils.RaycastNonSelf(_mod, origin, Vector3.down, out RaycastHit rayHit, GroundProbeSpan * 2f))
                return pelvisPos.y - rayHit.point.y;

            return 999f;
        }

        private float GetBodyHeight()
        {
            return _mod.CachedRig?.m_pelvis != null ? _mod.CachedRig.m_pelvis.position.y : 1.5f;
        }

        private Vector3 GetVelocity()
        {
            var rb = _mod.CachedPelvisRb;
            if (rb == null) return Vector3.zero;

            Vector3 v = rb.velocity;
            return PayneLabUtils.IsFinite(v) ? v : Vector3.zero;
        }

        private float GetTorsoPitch()
        {
            var rb = _mod.CachedSpineRb ?? _mod.CachedPelvisRb;
            if (rb == null) return 0f;
            return Mathf.Asin(Mathf.Clamp(rb.transform.forward.y, -1f, 1f)) * Mathf.Rad2Deg;
        }

        private float GetSpineRoll()
        {
            var rb = _mod.CachedSpineRb ?? _mod.CachedPelvisRb;
            if (rb == null) return 0f;
            return Mathf.Asin(Mathf.Clamp(rb.transform.right.y, -1f, 1f)) * Mathf.Rad2Deg;
        }

        private float GetSpineUpDot()
        {
            var rb = _mod.CachedSpineRb ?? _mod.CachedPelvisRb;
            if (rb == null) return 1f;
            return Vector3.Dot(rb.transform.up, Vector3.up);
        }

        private Vector3 GetFacingDirection()
        {
            if (Player.RigManager == null || Player.RigManager.physicsRig == null)
                return Vector3.forward;

            Vector3 dir = Player.RigManager.physicsRig.m_head.transform.forward;
            dir.y = 0f;
            return dir.sqrMagnitude > 0.001f ? dir.normalized : Vector3.forward;
        }

        private void SetRigKinematic(PhysicsRig rig, bool kinematic)
        {
            var rbs = rig.GetComponentsInChildren<Rigidbody>();
            foreach (var rb in rbs)
                rb.isKinematic = kinematic;
        }
    }
}