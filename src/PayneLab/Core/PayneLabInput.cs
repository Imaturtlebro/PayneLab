using BoneLib;
using UnityEngine;
namespace PayneLab
{
    public class PayneLabInput
    {
        private readonly PayneLabMod _mod;

        // Thumbstick values (read every frame)
        public Vector2 LeftStick;
        public Vector2 RightStick;

        // Stick click state
        public bool LStickDown;
        public bool RStickDown;
        public bool LStickHeld;
        public bool RStickHeld;
        public bool LStickUp;
        public bool RStickUp;

        // Face buttons (confirmed BoneLib API)
        public bool BDown;   // Right controller B
        public bool YDown;   // Left controller A (maps to Y on Quest)
        public bool BHeld;   // Right controller B held (wallrun intent button)

        // Timing for dual-stick detection
        private bool _rStickWasDown;
        private bool _prevLStickHeld;
        private bool _prevRStickHeld;
        private float _lStickDownTime;
        private float _rStickDownTime;
        private const float DualStickWindow = 0.20f;
        private const float KickHoldMin = 0.15f;
        private bool _blockKick;

        // State
        public bool WantRagdollToggle;
        public bool WantModeSwitch;
        public bool WantKick;
        public bool WantDive;
        public bool WantGroundRoll;
        public bool WantGetUp;
        public bool JumpPressed;
        public bool JumpHeld;

        /// <summary>
        /// Wallrun intent: the player is deliberately holding the right B button
        /// AND pushing forward on the left stick. Used (with airborne + wall
        /// proximity) as the master wall-run trigger, and release aborts the run.
        /// </summary>
        public bool WallrunIntentHeld => BHeld && LeftStick.y > 0.6f;

        public PayneLabInput(PayneLabMod mod) { _mod = mod; }

        /// <summary>
        /// Clear all one-shot wants. Called after the state machine ticks each
        /// physics frame. Input is refreshed once per rendered frame (OnUpdate),
        /// but Bonelab on Quest runs physics at ~90Hz and can issue multiple fixed
        /// updates per frame — without this, a single RS/LS click fired the same
        /// want two or three times in a row (all those "Manually Went Limp" bursts).
        /// </summary>
        public void ConsumeOneShots()
        {
            WantRagdollToggle = false;
            WantModeSwitch = false;
            WantKick = false;
            WantDive = false;
            WantGroundRoll = false;
            WantGetUp = false;
            JumpPressed = false;
        }

        public string InputStatus()
        {
            return $"Hp={(JumpHeld ? 1 : 0)} Jp={(JumpPressed ? 1 : 0)} " +
                   $"B={(BHeld ? 1 : 0)} wr={(WallrunIntentHeld ? 1 : 0)} " +
                   $"ls=({LeftStick.x:0.00},{LeftStick.y:0.00}) rs=({RightStick.x:0.00},{RightStick.y:0.00}) " +
                   $"rag={(WantRagdollToggle ? 1 : 0)} ms={(WantModeSwitch ? 1 : 0)} " +
                   $"kick={(WantKick ? 1 : 0)} dive={(WantDive ? 1 : 0)} roll={(WantGroundRoll ? 1 : 0)} " +
                   $"getup={(WantGetUp ? 1 : 0)}";
        }

        public void Tick()
        {
            WantRagdollToggle = false;
            WantModeSwitch = false;
            WantKick = false;
            WantDive = false;
            WantGroundRoll = false;
            WantGetUp = false;
            JumpPressed = false;
            JumpHeld = false;

            var lCtrl = Player.LeftController;
            var rCtrl = Player.RightController;
            if (lCtrl == null || rCtrl == null) return;

            float now = Time.time;

            // Read raw stick
            LeftStick = lCtrl.GetThumbStickAxis();
            RightStick = rCtrl.GetThumbStickAxis();

            // Read click state (held level). BoneLib's GetThumbStickDown/Up misbehave
            // on Quest and can report "down" for every frame the stick stays pressed,
            // so press/release edges are derived here from the previous held state.
            LStickHeld = lCtrl.GetThumbStick();
            RStickHeld = rCtrl.GetThumbStick();
            LStickDown = LStickHeld && !_prevLStickHeld;
            RStickDown = RStickHeld && !_prevRStickHeld;
            LStickUp = !LStickHeld && _prevLStickHeld;
            RStickUp = !RStickHeld && _prevRStickHeld;
            _prevLStickHeld = LStickHeld;
            _prevRStickHeld = RStickHeld;

            BDown = rCtrl.GetBButtonDown();
            YDown = lCtrl.GetAButtonDown();
            BHeld = rCtrl.GetBButton();
            JumpPressed = rCtrl.GetAButtonDown() && !LStickHeld;
            JumpHeld = rCtrl.GetAButton();

            // Track down timing for click-vs-hold discrimination
            if (LStickDown) _lStickDownTime = now;
            if (RStickDown) { _rStickWasDown = true; _rStickDownTime = now; _blockKick = false; }
            if (RStickUp) _rStickWasDown = false;

            // === DUAL STICK: Toggle ragdoll mode ===
            bool dualStick = false;
            if (RStickDown && LStickHeld && (now - _lStickDownTime) <= DualStickWindow) dualStick = true;
            if (LStickDown && RStickHeld && (now - _rStickDownTime) <= DualStickWindow) dualStick = true;

            if (dualStick)
            {
                _blockKick = true;
                WantRagdollToggle = true;
            }

            // === HOLD LS + Y: Switch input mode (ProcLeg / FullRagdoll / FullIK) ===
            // B on the right controller is now the wallrun intent button, so only
            // the left-controller face button (Y) cycles modes: hold LS + click Y.
            if (LStickHeld && YDown && !dualStick)
            {
                WantModeSwitch = true;
            }

            // === RS PRESS: Dive (standing) or Get-up (grounded / limp) ===
            // A true press edge, so one click can never cascade into both actions.
            if (RStickDown && !LStickHeld)
            {
                if (_mod.CurrentState == StuntState.Standing || _mod.CurrentState == StuntState.Aerial)
                {
                    WantDive = true;
                }
                else if (_mod.ActiveInput == InputMode.FullRagdoll ||
                         _mod.StateMachine.IsGroundedState(_mod.CurrentState))
                {
                    WantGetUp = true;
                }
            }

            // === RS RELEASE (short hold): Kick when standing ===
            if (RStickUp)
            {
                float held = now - _rStickDownTime;
                if (!_blockKick && _rStickWasDown && held >= KickHoldMin && _mod.CurrentState == StuntState.Standing)
                {
                    WantKick = true;
                }
            }

            // === LS TAP (not held, short): Ground roll when down ===
            if (LStickDown && !RStickHeld)
            {
                if (_mod.CurrentState == StuntState.KneeSlide ||
                    _mod.CurrentState == StuntState.ButtSlide ||
                    _mod.CurrentState == StuntState.DiveProne ||
                    _mod.CurrentState == StuntState.GroundRoll)
                {
                    WantGroundRoll = true;
                }
            }
        }
    }
}
