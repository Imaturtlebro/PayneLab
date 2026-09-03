using UnityEngine;
using Il2CppSLZ.Marrow;
using System.Collections.Generic;

namespace PayneLab
{
    public class PayneLabJointControl
    {
        private readonly PayneLabMod _mod;

        internal ConfigurableJoint[] HipJoints;
        internal ConfigurableJoint[] KneeJoints;
        internal bool DrivesCached => _drivesCached;
        internal int HipCount => HipJoints != null ? HipJoints.Length : 0;
        internal int KneeCount => KneeJoints != null ? KneeJoints.Length : 0;
        private bool _drivesCached;

        internal float HipSpringNow => _hipSpringNow;
        internal float HipDamperNow => _hipDamperNow;
        internal float KneeSpringNow => _kneeSpringNow;
        internal float KneeDamperNow => _kneeDamperNow;
        internal bool LimpApplied => _limpApplied;

        private ConfigurableJoint[] _allJoints;
        private JointDrive[] _allDriveSnapshot;
        private RotationDriveMode[] _allDriveModeSnapshot;
        private bool _snapshotTaken;
        private bool _limpApplied;

        private float _hipSpringNow, _hipDamperNow, _hipForceNow;
        private float _kneeSpringNow, _kneeDamperNow, _kneeForceNow;
        private const float DriveSmoothingRate = 6f;

        public PayneLabJointControl(PayneLabMod mod) { _mod = mod; }

        public void InvalidateCache()
        {
            _drivesCached = false;
            HipJoints = null;
            KneeJoints = null;
            _allJoints = null;
            _allDriveSnapshot = null;
            _allDriveModeSnapshot = null;
            _snapshotTaken = false;
            _limpApplied = false;

            if (_mod.LegPoses != null)
                _mod.LegPoses.ResetPoseTargets();
        }

        public void CacheJointDrives()
        {
            var rig = _mod.CachedRig;
            if (rig == null || _drivesCached) return;

            var hips = new List<ConfigurableJoint>();
            var knees = new List<ConfigurableJoint>();
            AddUnique(hips, GetJointOrNull(rig.m_hipLf));
            AddUnique(hips, GetJointOrNull(rig.m_hipRt));
            AddUnique(knees, GetJointOrNull(rig.m_kneeLf));
            AddUnique(knees, GetJointOrNull(rig.m_kneeRt));

            if (hips.Count < 2) AddRangeUnique(hips, GetJointsByName(rig, "hip", "thigh", "leg"));
            if (knees.Count < 2) AddRangeUnique(knees, GetJointsByName(rig, "knee", "calf", "shin"));

            HipJoints = hips.Count > 0 ? hips.ToArray() : null;
            KneeJoints = knees.Count > 0 ? knees.ToArray() : null;

            CaptureFullBodySnapshot();

            if (HipJoints != null && HipJoints.Length >= 2 && KneeJoints != null && KneeJoints.Length >= 2)
            {
                _drivesCached = true;
                ResetSmoothingToDefaults();
                LogJointNames();
            }
        }

        private void CaptureFullBodySnapshot()
        {
            var all = _mod.CachedRig.GetComponentsInChildren<ConfigurableJoint>();
            var list = new List<ConfigurableJoint>();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null) list.Add(all[i]);
            }
            _allJoints = list.ToArray();
            _allDriveSnapshot = new JointDrive[_allJoints.Length];
            _allDriveModeSnapshot = new RotationDriveMode[_allJoints.Length];
            for (int i = 0; i < _allJoints.Length; i++)
            {
                var j = _allJoints[i];
                var drive = j.slerpDrive;
                _allDriveSnapshot[i] = drive;
                _allDriveModeSnapshot[i] = j.rotationDriveMode;
            }
            _snapshotTaken = true;
        }

        private ConfigurableJoint GetJointOrNull(Transform bone)
        {
            return bone != null ? bone.GetComponent<ConfigurableJoint>() : null;
        }

        private void AddUnique(List<ConfigurableJoint> list, ConfigurableJoint joint)
        {
            if (joint == null) return;
            int id = joint.GetInstanceID();
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].GetInstanceID() == id) return;
            }
            list.Add(joint);
        }

        private void AddRangeUnique(List<ConfigurableJoint> list, ConfigurableJoint[] joints)
        {
            if (joints == null) return;
            foreach (var joint in joints) AddUnique(list, joint);
        }

        private void ResetSmoothingToDefaults()
        {
            float mult = _mod.JointSpringMultiplier;
            _hipSpringNow = 600f * mult; _hipDamperNow = 80f * mult; _hipForceNow = 1400f * mult;
            _kneeSpringNow = 400f * mult; _kneeDamperNow = 60f * mult; _kneeForceNow = 1100f * mult;
        }

        private void LogJointNames()
        {
            if (!_mod.Diagnostics) return;

            var hipNames = new List<string>();
            var kneeNames = new List<string>();
            foreach (var j in HipJoints) if (j != null) hipNames.Add(j.gameObject.name);
            foreach (var j in KneeJoints) if (j != null) kneeNames.Add(j.gameObject.name);

            _mod.LoggerInstance.Msg($"[PayneLab] Joint cache: hips=[{string.Join(", ", hipNames)}] knees=[{string.Join(", ", kneeNames)}]");
        }

        public void OnStateChanged(StuntState from, StuntState to)
        {
            if (!_drivesCached)
                CacheJointDrives();

            ApplyStateDriveParams(to);
        }

        public void Tick()
        {
            if (_mod.ActiveInput == InputMode.FullRagdoll)
            {
                ApplyFullBodyLimp();
                _limpApplied = true;
                return;
            }

            if (_limpApplied)
            {
                RestoreFullBodyDrives();
                _limpApplied = false;
                ApplyStateDriveParams(_mod.CurrentState);
            }

            var rb = _mod.CachedPelvisRb;
            if (rb == null) return;

            bool isGrounded = _mod.StateMachine != null && _mod.StateMachine.IsGrounded();

            // Aerial flips via Right Stick: strictly allowed only when airborne.
            // When touching ground in DiveProne, aerial torque is disabled so the torso
            // is never forced into the ground geometry.
            if (!isGrounded &&
                (_mod.CurrentState == StuntState.Aerial ||
                 _mod.CurrentState == StuntState.Cannonball ||
                 _mod.CurrentState == StuntState.DiveProne))
            {
                ApplyAerialTorque();
            }

            // Ground / dive barrel roll via Left Stick X (roll about spine forward axis)
            if (_mod.CurrentState == StuntState.GroundRoll ||
                _mod.CurrentState == StuntState.KneeSlide ||
                _mod.CurrentState == StuntState.ButtSlide ||
                _mod.CurrentState == StuntState.SideLying ||
                _mod.CurrentState == StuntState.DiveProne)
            {
                ApplyGroundRollTorque();
            }
        }

        // ── FULL-BODY LIMP / RESTORE ───────────────────────────────

        private void ApplyFullBodyLimp()
        {
            if (!_snapshotTaken || _allJoints == null) return;

            for (int i = 0; i < _allJoints.Length; i++)
            {
                var joint = _allJoints[i];
                if (joint == null) continue;

                JointDrive drive = joint.slerpDrive;
                drive.positionSpring = 40f;
                drive.positionDamper = 15f;
                drive.maximumForce = 250f;
                joint.slerpDrive = drive;
            }
        }

        private void RestoreFullBodyDrives()
        {
            if (!_snapshotTaken || _allJoints == null) return;

            for (int i = 0; i < _allJoints.Length; i++)
            {
                var joint = _allJoints[i];
                if (joint == null) continue;

                joint.slerpDrive = _allDriveSnapshot[i];
                joint.rotationDriveMode = _allDriveModeSnapshot[i];
            }
        }

        // ── TORQUE APPLICATION ──────────────────────────────────────

        private const float AerialTorqueGain = 10f;
        private const float MaxAerialAngularAccel = 300f;
        private const float GroundRollTorqueGain = 10f;
        private const float MaxGroundRollAngularAccel = 300f;

        private void ApplyAerialTorque()
        {
            var rb = _mod.CachedPelvisRb;
            if (rb == null) return;

            if (!PayneLabUtils.IsFinite(rb.position) || !PayneLabUtils.IsFinite(rb.velocity) ||
                !PayneLabUtils.IsFinite(rb.angularVelocity)) return;

            Vector2 rs = _mod.Input.RightStick;

            Vector3 desiredLocalAngVel = new Vector3(
                rs.y * _mod.AerialFlipTorque,
                0f,
                -rs.x * _mod.AerialFlipTorque
            );

            Vector3 currentLocalAngVel = rb.transform.InverseTransformDirection(rb.angularVelocity);
            Vector3 error = desiredLocalAngVel - currentLocalAngVel;

            Vector3 localTorque = Vector3.ClampMagnitude(error * AerialTorqueGain, MaxAerialAngularAccel);

            if (localTorque.sqrMagnitude > 0.01f)
            {
                rb.AddRelativeTorque(localTorque, ForceMode.Acceleration);
            }
        }

        private void ApplyGroundRollTorque()
        {
            var rb = _mod.CachedPelvisRb;
            if (rb == null) return;

            if (!PayneLabUtils.IsFinite(rb.position) || !PayneLabUtils.IsFinite(rb.velocity) ||
                !PayneLabUtils.IsFinite(rb.angularVelocity)) return;

            Vector2 ls = _mod.Input.LeftStick;

            float desiredRollRate = Mathf.Abs(ls.x) > 0.15f ? (-ls.x * _mod.GroundRollTorque) : 0f;
            float currentRollRate = Vector3.Dot(rb.transform.InverseTransformDirection(rb.angularVelocity), Vector3.forward);
            float rollTorque = Mathf.Clamp((desiredRollRate - currentRollRate) * GroundRollTorqueGain,
                -MaxGroundRollAngularAccel, MaxGroundRollAngularAccel);

            if (Mathf.Abs(rollTorque) > 0.01f)
            {
                rb.AddRelativeTorque(Vector3.forward * rollTorque, ForceMode.Acceleration);
            }
        }

        // ── DRIVE PARAMETER MANAGEMENT ──────────────────────────────

        public void ApplyStateDriveParams(StuntState state)
        {
            if (!_drivesCached) CacheJointDrives();

            float hipSpring, hipDamper, hipMaxForce;
            float kneeSpring, kneeDamper, kneeMaxForce;

            switch (state)
            {
                case StuntState.Standing:
                    hipSpring = 600f; hipDamper = 80f; hipMaxForce = 1400f;
                    kneeSpring = 400f; kneeDamper = 60f; kneeMaxForce = 1100f;
                    break;

                case StuntState.KneeSlide:
                    hipSpring = 400f; hipDamper = 80f; hipMaxForce = 800f;
                    kneeSpring = 700f; kneeDamper = 100f; kneeMaxForce = 1400f;
                    break;

                case StuntState.ButtSlide:
                    hipSpring = 350f; hipDamper = 60f; hipMaxForce = 700f;
                    kneeSpring = 300f; kneeDamper = 50f; kneeMaxForce = 600f;
                    break;

                case StuntState.Cannonball:
                    hipSpring = 800f; hipDamper = 90f; hipMaxForce = 1500f;
                    kneeSpring = 700f; kneeDamper = 80f; kneeMaxForce = 1500f;
                    break;

                case StuntState.SideLying:
                    // Soft grounded compliance matching DiveProne so state switches never kick
                    hipSpring = 320f; hipDamper = 50f; hipMaxForce = 650f;
                    kneeSpring = 220f; kneeDamper = 40f; kneeMaxForce = 500f;
                    break;

                case StuntState.DiveProne:
                    hipSpring = 300f; hipDamper = 45f; hipMaxForce = 600f;
                    kneeSpring = 200f; kneeDamper = 35f; kneeMaxForce = 450f;
                    break;

                case StuntState.GroundRoll:
                    hipSpring = 700f; hipDamper = 80f; hipMaxForce = 1400f;
                    kneeSpring = 600f; kneeDamper = 70f; kneeMaxForce = 1200f;
                    break;

                case StuntState.Aerial:
                    hipSpring = 600f; hipDamper = 60f; hipMaxForce = 1200f;
                    kneeSpring = 500f; kneeDamper = 50f; kneeMaxForce = 1000f;
                    break;

                case StuntState.WallRun:
                    hipSpring = 700f; hipDamper = 60f; hipMaxForce = 1400f;
                    kneeSpring = 500f; kneeDamper = 50f; kneeMaxForce = 1000f;
                    break;

                case StuntState.Kick:
                    hipSpring = 900f; hipDamper = 80f; hipMaxForce = 1800f;
                    kneeSpring = 750f; kneeDamper = 70f; kneeMaxForce = 1500f;
                    break;

                case StuntState.GetUp:
                    hipSpring = 800f; hipDamper = 80f; hipMaxForce = 1600f;
                    kneeSpring = 700f; kneeDamper = 70f; kneeMaxForce = 1400f;
                    break;

                default:
                    hipSpring = 600f; hipDamper = 80f; hipMaxForce = 1400f;
                    kneeSpring = 400f; kneeDamper = 60f; kneeMaxForce = 1100f;
                    break;
            }

            float mult = _mod.JointSpringMultiplier;
            float rate = Mathf.Clamp(Time.fixedDeltaTime * DriveSmoothingRate, 0f, 1f);

            _hipSpringNow = Mathf.Lerp(_hipSpringNow, hipSpring * mult, rate);
            _hipDamperNow = Mathf.Lerp(_hipDamperNow, hipDamper * mult, rate);
            _hipForceNow = Mathf.Lerp(_hipForceNow, hipMaxForce * mult, rate);
            _kneeSpringNow = Mathf.Lerp(_kneeSpringNow, kneeSpring * mult, rate);
            _kneeDamperNow = Mathf.Lerp(_kneeDamperNow, kneeDamper * mult, rate);
            _kneeForceNow = Mathf.Lerp(_kneeForceNow, kneeMaxForce * mult, rate);

            SetDriveArray(HipJoints, _hipSpringNow, _hipDamperNow, _hipForceNow);
            SetDriveArray(KneeJoints, _kneeSpringNow, _kneeDamperNow, _kneeForceNow);
        }

        private void SetDriveArray(ConfigurableJoint[] joints, float spring, float damper, float maxForce)
        {
            if (joints == null) return;

            for (int i = 0; i < joints.Length; i++)
            {
                var joint = joints[i];
                if (joint == null) continue;

                JointDrive drive = joint.slerpDrive;
                drive.positionSpring = spring;
                drive.positionDamper = damper;
                drive.maximumForce = maxForce;
                joint.slerpDrive = drive;
                joint.rotationDriveMode = RotationDriveMode.Slerp;
            }
        }

        // ── HELPERS ──────────────────────────────────────────────────

        private ConfigurableJoint[] GetJointsByName(PhysicsRig rig, params string[] nameContains)
        {
            var allJoints = rig.GetComponentsInChildren<ConfigurableJoint>();
            var result = new List<ConfigurableJoint>();

            foreach (var joint in allJoints)
            {
                string n = joint.gameObject.name.ToLower();
                foreach (var match in nameContains)
                {
                    if (n.Contains(match))
                    {
                        result.Add(joint);
                        break;
                    }
                }
            }

            return result.ToArray();
        }
    }
}