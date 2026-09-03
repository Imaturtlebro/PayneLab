using MelonLoader;
using BoneLib;
using BoneLib.BoneMenu;
using UnityEngine;
using Il2CppInterop.Runtime.Injection;
using Il2CppSLZ.Marrow;
using Il2CppSLZ.VRMK;
using System.Collections.Generic;

[assembly: MelonInfo(typeof(PayneLab.PayneLabMod), "PayneLab", "0.1.0", "Dillo")]
[assembly: MelonGame("Stress Level Zero", "BONELAB")]

namespace PayneLab
{
    public class PayneLabMod : MelonMod
    {
        public static PayneLabMod Instance { get; private set; }

        public bool ModEnabled = true;

        public StuntState CurrentState = StuntState.Standing;
        public InputMode ActiveInput = InputMode.ProcLeg;

        // Tuning
        public float StabilizerSpring = 800f;
        public float StabilizerDamping = 80f;
        public float StabilizerMaxTorque = 250f;
        public float JointSpringMultiplier = 1.0f;
        public float AerialFlipTorque = 12f;
        public float GroundRollTorque = 8f;
        public float DiveImpulseForce = 8f;
        public float SlideFriction = 0.05f;
        public float GroundRayDistance = 1.2f;
        public bool LegBendInvert = false;
        public float KneeSlideSpeedThreshold = 2.0f;
        public float ButtSlidePitchThreshold = 30f;
        public float WallrunGravityScale = 0.15f;
        public float WallJumpForce = 8.0f;
        public float WallrunMaxDuration = 2.5f;
        public float WallProbeDistance = 1.2f;
        public bool Diagnostics = true;

        private float _diagTimer;
        private const float DiagInterval = 1f;

        // Last sane physics frame, restored when a torque/dive explosion NaNs the
        // pelvis position (Latest log showed pos=NaN after a directional dive).
        private Vector3 _lastSanePosition;
        private bool _hasSanePosition;
        private const float MaxSaneVelocity = 35f;
        private const float MaxSaneAngularVelocity = 25f; // rad/s, generous but bounded

        // Modules
        internal PayneLabInput Input;
        internal PayneLabStateMachine StateMachine;
        internal PayneLabLegPoses LegPoses;
        internal PayneLabJointControl JointControl;
        internal PayneLabStabilizer Stabilizer;
        internal PayneLabObstacleClearance ObstacleClearance;

        // Cached Rig Bodies
        internal PhysicsRig CachedRig;
        internal Rigidbody CachedPelvisRb;
        internal Rigidbody CachedSpineRb;
        internal Rigidbody CachedHeadRb;
        internal Rigidbody CachedFootRb;
        internal Rigidbody CachedKneeRb;
        internal HashSet<int> CachedSelfColliders;

        // Physics-contact ground/wall truth, fed by PayneLabContactProbe.
        // The world floor/walls reject every Physics query (raycast/capsule/overlap),
        // so these collision flags are the authoritative surface source.
        internal bool ContactHasGround;
        internal bool ContactHasWall;
        internal Vector3 ContactWallNormal;

        private Il2CppSLZ.VRMK.Avatar _lastAvatar;

        public override void OnInitializeMelon()
        {
            Instance = this;

            // MUST register custom MonoBehaviours before the first GetComponent /
            // AddComponent touches them: on the stripped Quest IL2CPP build an
            // unregistered type throws TypeInitializationException at
            // GameObject.GetComponent<PayneLabContactProbe>, which silently killed
            // the whole contact probe (log: ct=[G:False W:False] forever, wallrun
            // never triggered, wall=F all game).
            try
            {
                ClassInjector.RegisterTypeInIl2Cpp<PayneLabContactProbe>();
            }
            catch (System.Exception ex)
            {
                LoggerInstance.Error($"[PayneLab] IL2CPP type registration failed (PayneLabContactProbe): {ex}");
            }

            Input = new PayneLabInput(this);
            StateMachine = new PayneLabStateMachine(this);
            LegPoses = new PayneLabLegPoses(this);
            JointControl = new PayneLabJointControl(this);
            Stabilizer = new PayneLabStabilizer(this);
            ObstacleClearance = new PayneLabObstacleClearance(this);

            SetupBoneMenu();
            LoggerInstance.Msg("[PayneLab] Loaded. Active ragdoll stunt system initialized.");
            LoggerInstance.Msg($"[PayneLab] Diagnostics ON - runtime state logged every {DiagInterval:0}s to MelonLoader/Latest.log. Toggle via BoneMenu > PayneLab > Diagnostics.");
        }

        public override void OnUpdate()
        {
            if (!ModEnabled) return;
            Input.Tick();
        }

        public override void OnFixedUpdate()
        {
            if (!ModEnabled) return;

            CheckAvatarChanged();
            TryCacheRig();

            if (CachedRig == null) return;

            // Contact probe fills these during the physics step. StateMachine reads
            // them now (top of frame), then we clear so next step starts fresh.
            StateMachine.OnContactsUpdate(ContactHasGround, ContactHasWall, ContactWallNormal);
            ClearContacts();

            // Retry joint binding every frame until the avatar's limbs are populated
            // (Marrow builds the physics skeleton across multiple frames after swaps).
            JointControl.CacheJointDrives();

            StateMachine.Tick();
            StateMachine.TickKick();
            LegPoses.Tick();
            JointControl.Tick();
            Stabilizer.Tick();
            ObstacleClearance.Tick();

            SanitizePhysics();

            // Consume this physics batch's one-shot inputs so a want set during the
            // render-loop OnUpdate can't auto-fire again in the next physics tick.
            Input.ConsumeOneShots();

            TickDiagnostics();
        }

        /// <summary>
        /// Guards the physics sim against NaN / explode-y values. The stabilizer and
        /// dive/torque math can drive the pelvis rigidbody to infinities (Latest log:
        /// pos=NaN right after a dive), after which every downstream state read goes
        /// NaN forever. Zero bad velocity, clamp to a sane cap, and restore the last
        /// finite position so the physics sim can never spiral.
        /// </summary>
        private void SanitizePhysics()
        {
            // Full-body NaN sentry: inspect every tracked rigidbody and neutralize
            // any that explode, so a single bad bone can't take down the whole sim.
            SanitizeBody(CachedPelvisRb, "pelvis", true);
            SanitizeBody(CachedSpineRb, "spine", false);
            SanitizeBody(CachedHeadRb, "head", false);
            SanitizeBody(CachedFootRb, "foot", false);
            SanitizeBody(CachedKneeRb, "knee", false);
        }

        private void SanitizeBody(Rigidbody rb, string name, bool restorePosition)
        {
            if (rb == null) return;

            Vector3 pos = rb.position;
            bool posBad = !PayneLabUtils.IsFinite(pos);

            Vector3 vel = rb.velocity;
            bool velBad = !PayneLabUtils.IsFinite(vel);

            Vector3 angVel = rb.angularVelocity;
            bool angBad = !PayneLabUtils.IsFinite(angVel);

            if (posBad || velBad || angBad)
            {
                LoggerInstance.Warning($"[PayneLab][CRITICAL] Physics Explosion detected on {name}! " +
                    $"Pos: {Vec3(pos)} Vel: {Vec3(vel)} AngVel: {Vec3(angVel)}. Sanitizing...");
            }

            // Track the last healthy pelvis position so a NaN pelvis can be restored
            // to a sane spot instead of being dropped to the origin.
            if (restorePosition && !posBad)
            {
                _lastSanePosition = pos;
                _hasSanePosition = true;
            }

            if (posBad && restorePosition && _hasSanePosition)
                rb.position = _lastSanePosition;
            else if (posBad)
                rb.position = Vector3.zero;

            if (velBad)
                rb.velocity = Vector3.zero;
            else if (vel.sqrMagnitude > MaxSaneVelocity * MaxSaneVelocity)
                rb.velocity = vel.normalized * MaxSaneVelocity;

            if (angBad)
                rb.angularVelocity = Vector3.zero;
            else if (angVel.sqrMagnitude > MaxSaneAngularVelocity * MaxSaneAngularVelocity)
                rb.angularVelocity = angVel.normalized * MaxSaneAngularVelocity;
        }

        /// <summary>Called by PayneLabContactProbe for every valid world contact.</summary>
        internal void ReportContact(Vector3 point, Vector3 normal)
        {
            // Classify by direction: mostly-up = ground, mostly-horizontal = wall.
            if (normal.y > 0.5f)
            {
                ContactHasGround = true;
                return;
            }
            if (Mathf.Abs(normal.y) <= 0.5f)
            {
                ContactHasWall = true;
                ContactWallNormal = normal;
            }
        }

        private void ClearContacts()
        {
            ContactHasGround = false;
            ContactHasWall = false;
            ContactWallNormal = Vector3.zero;
        }

        private void TickDiagnostics()
        {
            if (!Diagnostics) return;

            _diagTimer += Time.fixedDeltaTime;
            if (_diagTimer < DiagInterval) return;
            _diagTimer = 0f;

            LoggerInstance.Msg(BuildDiagnosticsLine());
        }

        private string BuildDiagnosticsLine()
        {
            string rig = CachedRig != null ? "T" : "F";
            string avatar = "?";
            if (Player.RigManager != null && Player.RigManager.avatar != null)
                avatar = Player.RigManager.avatar.name;

            Vector3 vel = CachedPelvisRb != null ? CachedPelvisRb.velocity : Vector3.zero;
            if (!PayneLabUtils.IsFinite(vel)) vel = Vector3.zero;
            float mass = CachedPelvisRb != null ? CachedPelvisRb.mass : 0f;
            float friction = CurrentMatFriction();

            return $"[PayneLab][Heartbeat] State: {CurrentState} | InputMode: {ActiveInput} | " +
                   $"Mass: {mass:0.0}kg | PelvisVel: ({vel.x:0.00}, {vel.y:0.00}, {vel.z:0.00}) | " +
                   $"DistToGround: {StateMachine.GetGroundDistance():0.00}m | " +
                   $"Friction: {friction:0.00} | Hips: [{JointControl.HipSpringNow:0}/{JointControl.HipDamperNow:0}] " +
                   $"Knees: [{JointControl.KneeSpringNow:0}/{JointControl.KneeDamperNow:0}] | " +
                   $"rig={rig} joints=[H{JointControl.HipCount} K{JointControl.KneeCount}] av={avatar} " +
                   $"af={StateMachine.AirborneFrames} wall={(StateMachine.IsWallRunning ? "T" : "F")} " +
                   $"ct=[G:{ContactHasGround} W:{ContactHasWall}] lock={StateMachine.ActionLockTimer:0.00} " +
                   $"in=[{Input.InputStatus()}]";
        }

        // Best-effort read of the pelvis collider's current dynamic friction, so the
        // heartbeat can confirm the slide material is engaged (0.00) or restored (1.00).
        private float CurrentMatFriction()
        {
            if (CachedPelvisRb == null) return -1f;
            try
            {
                var col = CachedPelvisRb.GetComponent<Collider>();
                if (col == null || col.material == null) return -1f;
                return col.material.dynamicFriction;
            }
            catch (System.Exception)
            {
                return -1f;
            }
        }

        private string Vec3(Vector3 v)
        {
            return $"{v.x:0.00},{v.y:0.00},{v.z:0.00}";
        }

        private void CheckAvatarChanged()
        {
            if (Player.RigManager == null) return;
            if (Player.RigManager.avatar != _lastAvatar)
            {
                _lastAvatar = Player.RigManager.avatar;
                InvalidateCache();
            }
        }

        internal void TryCacheRig()
        {
            if (CachedRig != null) return;
            if (Player.PhysicsRig == null) return;

            CachedRig = Player.PhysicsRig;

            // Marrow assembles the avatar bones/joints over several frames after a
            // swap. If any intermediate bone transform is still null, abandon the
            // partial cache this frame so we gracefully retry next frame instead of
            // NRE-spamming (which tanks the log and framerate).
            if (CachedRig.m_pelvis == null || CachedRig.m_head == null)
            {
                CachedRig = null;
                return;
            }

            CachedPelvisRb = CachedRig.m_pelvis.GetComponent<Rigidbody>();
            CachedHeadRb = CachedRig.m_head.GetComponent<Rigidbody>();
            CachedFootRb = CachedRig._feetRb;
            CachedKneeRb = CachedRig._kneeRb;

            var selfColliders = CachedRig.GetComponentsInChildren<Collider>();
            CachedSelfColliders = new HashSet<int>(selfColliders.Length);
            for (int i = 0; i < selfColliders.Length; i++)
                CachedSelfColliders.Add(selfColliders[i].GetInstanceID());

            // Attach the physics-contact probe to every rigidbody-bearing bone
            // (pelvis, head, spine, feet, knees...). OnCollisionStay only fires on
            // the GameObject that owns the collider/rigidbody, so a single probe on
            // the rig root would receive nothing. This gives contact truth on the
            // only mechanism that survives this build (physics queries fail on it).
            var rbs = CachedRig.GetComponentsInChildren<Rigidbody>();
            for (int i = 0; i < rbs.Length; i++)
            {
                var go = rbs[i].gameObject;
                if (go == null) continue;
                var probe = go.GetComponent<PayneLabContactProbe>();
                if (probe == null)
                {
                    probe = go.AddComponent<PayneLabContactProbe>();
                    probe.Mod = this;
                }
            }

            // Find spine via pelvis rigidbody
            if (CachedPelvisRb != null)
            {
                var spineJoint = CachedPelvisRb.GetComponentInChildren<ConfigurableJoint>();
                if (spineJoint != null && spineJoint.connectedBody != null)
                    CachedSpineRb = spineJoint.connectedBody;
            }

            JointControl.CacheJointDrives();
            LoggerInstance.Msg("[PayneLab] Physics rig & leg drives cached.");
        }

        internal void InvalidateCache()
        {
            // Restore standard collider friction so the old rig's slide material
            // isn't left stuck on any shared/materialized collider after an avatar
            // swap or scene change.
            try
            {
                ObstacleClearance?.RestoreFriction();
            }
            catch (System.Exception)
            {
            }

            CachedRig = null;
            CachedPelvisRb = null;
            CachedSpineRb = null;
            CachedHeadRb = null;
            CachedFootRb = null;
            CachedKneeRb = null;
            CachedSelfColliders = null;
            JointControl.InvalidateCache();

            // If the player was mid-limp/ragdoll on the old avatar, forcibly settle
            // back to a clean procedural standing baseline. The new avatar inherits a
            // fresh set of joints and drives (no stale _limpApplied carrying over).
            ActiveInput = InputMode.ProcLeg;
            CurrentState = StuntState.Standing;
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            InvalidateCache();
            CurrentState = StuntState.Standing;
            ActiveInput = InputMode.ProcLeg;
        }

        public void SetState(StuntState newState)
        {
            if (newState == CurrentState) return;

            var prev = CurrentState;
            CurrentState = newState;

            JointControl.OnStateChanged(prev, newState);
            LegPoses.OnStateChanged(prev, newState);

            // Slide/dive states ride the ground with low friction on the pelvis,
            // knee and foot colliders so the body skims instead of catching and
            // tumbling. WallRun also sets its own friction (0f) on entry. Entering
            // any of these engages it, leaving restores standard friction.
            bool enteringSlide = IsSlideState(newState);
            bool leavingSlide = IsSlideState(prev) && !enteringSlide;
            if (enteringSlide)
            {
                ObstacleClearance.SetSlideFriction(0f);
            }
            else if (leavingSlide)
            {
                ObstacleClearance.RestoreFriction();
            }

            // Any ground-contact stunt state rides with the physical legs and the
            // Marrow spherical locomotion ball OFF, so the ball-loco can't fight the
            // forced leg poses (the auto-slide gap: dive/kick/ragdoll already did
            // this manually, slides didn't). Returning to Standing reintegrates.
            if (newState == StuntState.Standing)
            {
                RestoreBallLocomotion();
            }
            else if (newState != StuntState.Standing && prev == StuntState.Standing)
            {
                DisableBallLocomotion();
            }

            LoggerInstance.Msg($"[PayneLab][State] {prev} -> {newState} | " +
                $"Spd: {HorizSpeed():0.00} m/s | TorsoPitch: {StateMachine.TorsoPitch:0.0}° | " +
                $"SpineRoll: {StateMachine.SpineRoll:0.0}° | Grounded: {StateMachine.IsGrounded()} " +
                $"(probe:{StateMachine.ContactGrounded}, vy:{StateMachine.PelvisVy:0.00}) | " +
                $"Lock: {StateMachine.ActionLockTimer:0.00}s");
        }

        private void DisableBallLocomotion()
        {
            var rig = CachedRig;
            if (rig == null) return;
            try
            {
                rig.PhysicalLegs();
                rig.DisableBallLoco();
            }
            catch (System.Exception)
            {
            }
        }

        private void RestoreBallLocomotion()
        {
            var rig = CachedRig;
            if (rig == null) return;
            try
            {
                rig.TurnOnRig();
                rig.UnRagdollRig();
                rig.EnableBallLoco();
            }
            catch (System.Exception)
            {
            }
        }

        private float HorizSpeed()
        {
            if (CachedPelvisRb == null) return 0f;
            var v = CachedPelvisRb.velocity;
            if (!PayneLabUtils.IsFinite(v)) return 0f;
            return new Vector2(v.x, v.z).magnitude;
        }

        private static bool IsSlideState(StuntState state)
        {
            return state == StuntState.KneeSlide ||
                   state == StuntState.ButtSlide ||
                   state == StuntState.DiveProne ||
                   state == StuntState.SideLying ||
                   state == StuntState.GroundRoll;
        }

        private void SetupBoneMenu()
        {
            if (_boneMenuBuilt) return;
            _boneMenuBuilt = true;
            try
            {
                SetupBoneMenuCore();
            }
            catch (System.Exception ex)
            {
                LoggerInstance.Error($"[PayneLab] BoneMenu setup skipped (non-fatal): {ex}");
            }
        }

        private bool _boneMenuBuilt;

        private void SetupBoneMenuCore()
        {
            var page = Page.Root.CreatePage("PayneLab", Color.red);
            page.CreateBool("Enabled", Color.white, true, v => ModEnabled = v);
            page.CreateFloat("Stabilizer Spring", Color.yellow, StabilizerSpring, 50f, 0f, 3000f, v => StabilizerSpring = v);
            page.CreateFloat("Stabilizer Damping", Color.yellow, StabilizerDamping, 5f, 0f, 500f, v => StabilizerDamping = v);
            page.CreateFloat("Stabilizer Max Torque", Color.yellow, StabilizerMaxTorque, 25f, 0f, 2000f, v => StabilizerMaxTorque = v);
            page.CreateFloat("Joint Spring Mult", Color.cyan, JointSpringMultiplier, 0.1f, 0.1f, 3f, v => JointSpringMultiplier = v);
            page.CreateFloat("Aerial Flip Torque", Color.green, AerialFlipTorque, 1f, 1f, 30f, v => AerialFlipTorque = v);
            page.CreateFloat("Ground Roll Torque", Color.green, GroundRollTorque, 1f, 1f, 20f, v => GroundRollTorque = v);
            page.CreateFloat("Dive Impulse", Color.magenta, DiveImpulseForce, 1f, 1f, 20f, v => DiveImpulseForce = v);
            page.CreateFloat("Slide Friction", Color.gray, SlideFriction, 0.01f, 0f, 1f, v => SlideFriction = v);
            page.CreateBool("Invert Leg Bend", Color.cyan, false, v => LegBendInvert = v);
            page.CreateFloat("Wallrun Gravity Scale", Color.blue, WallrunGravityScale, 0.05f, 0f, 1f, v => WallrunGravityScale = v);
            page.CreateFloat("Wall Jump Force", Color.blue, WallJumpForce, 0.5f, 1f, 15f, v => WallJumpForce = v);
            page.CreateFloat("Wallrun Max Duration", Color.blue, WallrunMaxDuration, 0.25f, 0.5f, 10f, v => WallrunMaxDuration = v);
            page.CreateBool("Diagnostics", Color.gray, Diagnostics, v => Diagnostics = v);
        }
    }

    public enum StuntState
    {
        Standing,
        KneeSlide,
        ButtSlide,
        Cannonball,
        SideLying,
        DiveProne,
        GroundRoll,
        WallRun,
        Aerial,
        Kick,
        GetUp
    }

    public enum InputMode
    {
        ProcLeg,
        FullRagdoll,
        FullIK
    }
}