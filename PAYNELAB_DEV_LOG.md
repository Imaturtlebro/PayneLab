# PayneLab: Developer Reflections & Design Philosophy

> **Status:** Active Development  
> **Core Focus:** High-fidelity physical character interaction, emergent movement, and "feel-first" design.

---

## 🧠 The Core Philosophy: "Physics First, Animation Second"

Most games treat physics as a fallback for when animations fail. **PayneLab flips this.** We start with raw, unadulterated physics and layer procedural animation on top to *enhance* realism, not replace it.

The goal isn't to make a character that looks perfect in a T-pose. The goal is to make a character that feels **alive** when they are tumbling down a mountain, diving off a cliff, or running up a wall. If the physics say the leg should bend weirdly because of an impact, we don't hide it—we make the muscle tension react to it so it looks *intentional*.

### The "Stiff-Limp" Paradox
One of our biggest breakthroughs was solving the "Ragdoll Problem."
- **Old Way:** Ragdolls are either solid statues (no reaction) or wet noodles (no structure).
- **PayneLab Way:** **Stiff-Limp.**
    - In **Ragdoll Mode (RS+LS)**, legs have high stiffness to maintain shape (no spaghetti legs) but low enough damping to react to hits. They hang heavy, swing with momentum, but don't collapse.
    - In **Dive Mode**, legs dynamically extend based on velocity. Fast dive = straight legs (Superman). Slow fall = relaxed tuck.
    - **Why it matters:** It sells the *weight* of the character. You feel the mass of the limbs fighting against gravity and air resistance.

---

## 🏃 Movement as Expression

We didn't just want "WASD to move." We wanted movement to be a language.

### 1. The Dive (RS Click)
This isn't just a "prone" state. It's a **flight simulator for your body**.
- **Control Scheme:**
    - **Right Stick:** Subtle steering (Yaw) and Torso tilt (Pitch). It feels like banking an airplane.
    - **Left Stick:** Aggressive acrobatics. Push forward for a tucked front flip; push left for a violent barrel roll.
- **The Feel:** When you tuck for a flip, the legs *must* come up. When you straighten out, they must snap back. The visual feedback loop (Input → Body Rotation → Leg Tuck) creates a sense of total aerial agency. You aren't just falling; you're piloting your fall.

### 2. Wall Running (Hold A/D + Jump)
Wall running often feels "sticky" or scripted in other games. Here, it's **magnetic but physical**.
- **The Mechanic:** You don't just "trigger" a wall run. You jump *near* a wall while holding toward it. The system magnetizes your feet to the surface normal.
- **The Animation:** The legs don't just freeze in a running cycle. They procedurally plant based on where the wall actually is. If you jump slightly high, the stride lengthens. If you slip, the feet scramble.
- **The Exit:** Letting go of the key drops you instantly. No forced "jump off" animation. Just pure gravity taking over.

### 3. The Get-Up
Transitioning from "dead weight" (Ragdoll/Dive) to "standing hero" is usually a canned animation.
- **Our Approach:** A two-phase procedural blend.
    1.  **The Tuck:** Knees drive up, hips flex, feet plant firmly under the center of mass.
    2.  **The Drive:** Hips extend, knees lock, torso rises.
- It syncs with the physics impulse, so if you get up on a slope, you actually push off the slope.

---

## 🎮 Control Scheme Highlights

The control map was designed to separate **Fine Control** from **Gross Motor Skills**.

| Input | Fine Control (Subtle) | Gross Motor (Aggressive) |
| :--- | :--- | :--- |
| **Right Stick (RS)** | **Steering & Posture.** Turn left/right, lean torso forward/back. Used for aiming your fall. | *N/A* |
| **Left Stick (LS)** | *N/A* | **Acrobatics.** Flips, barrel rolls, rapid spins. Used for style and momentum redirection. |
| **Shoulders/Triggers** | **Arm Positioning.** Active arm control in both Dive and Ragdoll modes to sell the effort. | |

**Key Insight:** Giving the player **arm control in Ragdoll mode** was crucial. Even when "limp," being able to position your arms makes the ragdoll feel like *your* body, not a dummy. It bridges the gap between "simulation" and "game."

---

## 🔮 Future Vision & Challenges

### What Works Well
- **The "Stiff-Limp" Leg Logic:** This is the secret sauce. It prevents the uncanny valley of floppy limbs while keeping the chaos of physics.
- **Dual-Stick Aerial Control:** Separating steering (RS) from flipping (LS) allows for complex corkscrews that feel intuitive, not confusing.
- **Procedural Wall Interaction:** No more clipping through walls or floating feet. The IK solves for the surface normal in real-time.

### Where We Can Push Further
- **Impact Reaction:** Currently, hits knock you back. Ideally, specific limb impacts should cause localized flinches without ruining the whole pose.
- **Surface Friction Visualization:** When wall running, the foot plants could show more "slip" before gripping, selling the friction coefficient of the material.
- **Momentum Conservation:** Transitions from wall-run to dive could preserve more horizontal velocity, making the flow state even faster.

---

## 🏁 Final Verdict

**PayneLab** is an experiment in **trusting the player**.

We trust the player to handle the chaos of physics. We trust them to learn the nuance of dual-stick aerial maneuvering. We trust that a "janky" looking tumble is better than a polished, fake animation if it *feels* real.

It's not just a movement mod; it's a **physics playground**. Whether you are diving headfirst off a skyscraper, rolling down a hill in ragdoll mode just to see how your legs bounce, or sprinting vertically up a sheer cliff face, the system is designed to say: *"Yes, and..."* to every input.

**Keep breaking it. Keep refining the feel. The jank is just unpolished genius.**

— *The PayneLab Dev Team*
