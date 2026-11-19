---
name: dfm-cnc-carving
description: >-
  Use this skill when designing, modifying, or auditing parts, fixtures,
  vacuum pods, acrylic blocks, Corian jigs, or carved wood components for your
  Onefinity Elite Journeyman Gen 2 CNC router.
---

# Design for CNC Carving (Onefinity Elite Journeyman Gen 2)

This is a **Tier 1 (Preferred In-House)** process in `shop_profile.json`. Default to **`IPS`** for furniture, shop jigs, and QCW fixtures (use `MMGS` only when explicitly machining a metric interface).

---

## 1. Machine Envelope & Hardware Capabilities

- **Machine**: **Onefinity Elite Journeyman Gen 2**
  - Cutting Envelope: **`48.0" (X) × 32.0" (Y) × 6.5" (Z)`**
  - Spindle & Tooling: **Redline Spindle** with **Automatic Tool Changer (ATC)**, **Rotary 4th Axis**, and **Air Assist Nozzle** (`9 CFM` supply).
- **Workholding Systems**:
  1. **Vacuum Clamping Setup**: Maximize unbroken bottom contact area. For through-cuts on small parts or cutouts that reduce vacuum surface area, design **`0.020"` onion-skin bottom layers** or explicit breakaway tabs.
  2. **`1/2"` Corian + MMA Glue Vacuum Pods**: When extreme rigidity or a 100% vacuum-impermeable fixture/suction pod is required, design pods from **`1/2"` Corian** laminated with **MMA structural glue**, with milled O-ring/gasket channels and tapped **`1/8" NPT`** vacuum ports (`0.332"` / `11/32"` tap drill, `>= 0.625"` boss wall).
  3. **QCW Frame `3/8"` Dowel Pin Grid**: Each QCW MDF slat (`~9"–10"` wide × `42"` long) has `3/8" (0.375")` dowel pin holes on a **`2.000" × 2.000"` grid**.
     - **Critical Rule**: Each slat's `2.000"` grid is independent from neighboring slats. Keep any pair of `3/8"` registration dowel holes **on a single slat (`<= 8.0"` span across the slat width)** unless probing both datums manually.

---

## 2. Exact 8-Bit Cutter Library & Geometry Rules

Every design MUST be machinable using ONLY your 8 owned CNC tools:

| Tool | Diameter | Key Specs | Primary Applications & DFM Constraints |
| :--- | :---: | :--- | :--- |
| **Cadence Mfg "The Jenny" Compression** | `1/4"` (`0.250"`) | `1/8"` upcut tip height | Plywood & two-sided hardwood profiles. **First pass MUST exceed `0.135"` depth** so the downcut flutes engage above the top veneer. |
| **SpeTool W02011 Long Compression** | `1/4"` (`0.250"`) | `1.250"` LOC, `3.0"` OAL | Deep pockets & thick hardwood/stock cuts up to **`1.250"`** deep. Avoid thin walls $> 4\times$ diameter without step-down support. |
| **Bits&Bits Downcut Spiral Astra HP** | `1/4"` (`0.250"`) | `1/4"` shank | Crisp top edges, shallow pockets, dados, rabbets, inlay pockets, gasket grooves. |
| **Cadence Mfg Ballnose** | `1/4"` (`0.250"`) | `R0.125"` tip | 3D carving, sculpted furniture legs/contours, flutes, rounded hollows. |
| **Amana #51402-Z ZrN Spiral O-Flute Upcut** | `1/4"` (`0.250"`) | `5/8" (0.625")` LOC | Dedicated cutter for **`1/8"` & `4"` Acrylic**, **`1/2"` Corian**, plastics, and soft aluminum **with Air Assist enabled**. Keep single-sided acrylic pocket depth $\le 0.625^{\prime\prime}$ unless stepped wider for collet/shank clearance. |
| **Freud 16-108 Surface Mortising** | `1-1/4"` (`1.250"`) | `1/2"` carbide height, `1/2"` shank | Spoilboard/slab flattening, wide flat-bottom mortising, large rabbets. Requires `R >= 0.650"` corners or cleanup pass with `1/4"` bit. |
| **Cadence Mfg "GrooVee Jenny" `90°` Downshear** | `3/8"` (`0.375"`) | `90°` downshear V-bit | **Preferred in wood & plywood** for chamfers, V-carved miter folds, and crisp tearout-free jig text labels (`sw_engrave_or_emboss_label`). |
| **`30° 0.2 mm` Tip V-Engraving Bit** | `0.2 mm` tip | `30°` included | Fine text/ticks on **`1/2"` Corian**, **Acrylic**, brass inlays, or micro-engraving. |

### Non-Negotiable Geometric Constraints (`1/4"` Min Flat/Spiral Cutter)
1. **No Sharp Internal Vertical Corners**: Because the smallest flat/compression/O-flute endmill is **`0.250"` (`1/4"`)**, EVERY internal vertical pocket corner MUST either:
   - Have fillet radius **`R >= 0.135"`** (prefer **`R0.150"`** to avoid `180°` full-radial tool chatter at `R0.125"`), **OR**
   - Include a **`0.250"` diameter dogbone / T-bone corner relief** (automatically handled for joinery via `SolidWorksJoineryAddin` or `sw_hole_preset`) whenever a square-cornered mating part drops into the pocket.
2. **Minimum Slot / Groove Width**: **`>= 0.250"`** (unless intentionally cut with the `90°` GrooVee Jenny or `30°` V-engraver).
3. **Pocket Depth Limits**: Max `1.250"` for wood (`SpeTool W02011`), max `0.625"` for single-setup acrylic/Corian (`Amana 51402-Z`) unless relieving walls for chuck clearance, or flip-machining via `3/8"` dowel pins on the rotary/QCW grid.
4. **Rotary 4th Axis Work**: For cylindrical legs, turned connectors, or round vacuum manifolds, include end-stock center alignment bosses and tailstock clearance.

---

## 3. Mandatory Pre-CAM Audit

Before exporting DXF/STEP for CAM toolpaths, run `sw_audit_dfm(process="cnc_carving")` to verify envelope fit (`<= 48"×32"×6.5"`), internal corner radii (`>= 0.135"` or dogbones), pocket depth limits against your 8 tools, and QCW single-slat dowel alignment.
