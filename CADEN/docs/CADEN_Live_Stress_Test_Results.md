# CADEN live Gemini stress-test report

Run: 2026-09-26. Configured model: `gemini-flash-latest` (alias, underlying model version not pinned). One sequential conversation for tests 1-21, then a new ChatSession plus issue/memory store reopen before test 22. Same active prompt, metadata and tool implementations as the desktop; no WinForms UI automation. Separate disk sidecars and a fresh host project association protected existing user memory/dispositions.

This is one live evaluation, not a statistically reliable general accuracy estimate. Exact prompts match the previous 25-item list. No live retries or fixes were applied. An initial sandbox connection attempt was blocked before a Gemini response; the authorized network-enabled run supplied the results below.

## Score

- First 20: **14 Pass, 5 Partial, 1 Fail**. Strict fully-correct rate: **70%**. All 20 produced answers. Partial means the main answer was useful but had an unsupported addition, omitted a required limitation, or misstated an availability distinction. Fail means a material unsupported engineering assertion or no user-facing answer. No arbitrary half-credit aggregate is used.
- Last 5: **4 Pass, 1 Fail (80% end-to-end)**. Memory survived reopening, but test 22 exhausted orchestration rounds before answering. Independent durable-state checks passed for the requested writes and final cleanup.
- Numerical mass, COM and L-inertia answers passed. Main weaknesses were unsupported explanatory additions, incomplete uncertainty reporting, excessive verbosity, and failure to stop after sufficient memory evidence.

## Per-test results

| Test | Grade | Assessment |
| --- | --- | --- |
| 1 | Partial | Correct model, counts, all-18 availability and partial scope. Overstates get_object_details as returning bounding dimensions; the implemented dimensions field is exported feature dimensions, not a dedicated bounding-box measurement capability. |
| 2 | Pass | Correctly discovers both cylinder-named occurrences with find_objects. |
| 3 | Partial | Mass is correct. Unsupported extra causal claim: 'mass reflects the document's default density.' Returned evidence establishes unassigned material and mass, not that source of density. |
| 4 | Pass | Correct unassigned document/body material answer, reusing the prior tool response without an unnecessary call. |
| 5 | Pass | Correct direct-child hierarchy and both part occurrences. |
| 6 | Pass | Correct threshold conversion and base-only result; reading the two already-discovered leaf parts is a valid alternative to a property search. |
| 7 | Pass | Correct false-versus-not_applicable distinction; root excluded. |
| 8 | Pass | Correct leaf sum and root comparison within displayed floating-point precision, without double-counting. |
| 9 | Pass | Correct COM values, millimeters and explicit SolidWorks root-document frame. |
| 10 | Pass | Correct Lxx/Lyy/Lzz values and units; Y/Z preserved. |
| 11 | Pass | Correctly rejects silently relabeling output-axis components as principal moments. |
| 12 | Pass | Correct two exported mates, types, false suppression, concentric lockRotation=false and coincident not_applicable. Could repeat the partial-export caveat more clearly, but does not assert solved constraint validity. |
| 13 | Partial | Correct one-hop path and parallel mates, with a no-rigidity caveat. Omits the specifically required partial-coverage and shortestPathComplete=false limitations from the returned tool result. |
| 14 | Pass | Correct zero-hop start-only result and no connecting mates. |
| 15 | Fail | Starts with the correct 'No', but asserts error code zero means the mate solved/rebuilt without errors and is not broken/dangling/conflicting. It also asserts an unconstrained rotational DOF. Unknown satisfaction, partial graph evidence and missing DOFs do not establish those claims. |
| 16 | Partial | Correctly states exact DOFs are unavailable, then volunteers a model-specific theoretical 1 rotational / 0 translational DOF account. It labels this unverified, but weakens the explicit no-inferred-count test boundary. |
| 17 | Partial | Correct three findings, severities and 9/18 incomplete checker-subject evaluations. Labels root definitionStatus as not_applicable although the canonical evidence says Missing/unknown. Also carries forward unlocked-motion language beyond the native under-defined finding. |
| 18 | Pass | Uses get_issue_evidence and correctly explains native under_defined, fixed=false, suppressed=false and design-intent classification. Rotation is framed as an illustrative possible intent rather than a solved count. |
| 19 | Pass | Revalidation commits, reports Complete/Present and retains Open against the unchanged snapshot. |
| 20 | Pass | Correctly refuses to invent interference or minimum-clearance results and cites disabled/unavailable analysis. |
| 21 | Pass | Read then write committed stress.rotation to object memory as Active/AssistantInferred. Independent state readback confirms all issue dispositions remain Open. |
| 22 | Fail | New chat and both stores reopened from disk. Persisted note was successfully retrieved, proving storage continuity, but the user received no answer. An invalid find_objects call without query/property wasted a round; further unnecessary queries hit the six-tool-round limit. The seventh emitted call was not executed. |
| 23 | Pass | Committed under-defined disposition Ignored with the given reason; independent state readback confirms it and leaves memory Active. |
| 24 | Pass | Correctly distinguishes Ignored tracking state from unchanged exported under_defined metadata, with a supporting property read. |
| 25 | Pass | Two independent successful actions reopen the issue and retire the memory. Final disk reopen confirms all three issues Open and stress.rotation Retired/AssistantInferred. |

## Test 22: persistence worked; orchestration failed

The fresh session first read project-only memory, then called find_objects without either a query or a property condition (INVALID_ARGUMENT). It used hierarchy to discover IDs, then retrieved the persisted object note successfully. Rather than answering that the note records intent and cannot prove motion, it fetched object properties and attempted another mate query. The client rejected that next call at the six-round limit. The note was never lost; the model did not finish its answer. Diagnostic ID: `0a28f93a11044886b2ed2591285129fb`.

Final independent readback after another disk reopen: all three issues are Open; stress.rotation is Retired with AssistantInferred provenance. Source CAD metadata was not modified. Tests ran only in isolated storage under CADEN/.tools/live-stress/runs/run02.

## API usage and verification limits

- 25 attempted turns; 24 returned answers; 60 actual HTTP requests to Gemini. Function calling requires multiple HTTP requests per user turn.
- 37 emitted function calls, 36 returned tool results. The final requested call in failed test 22 was not dispatched.
- Summed API usage: 1589193 input tokens, of which 1159698 were reported cached; 13669 candidate output tokens and 14884 thought tokens. These are provider-reported cumulative request counts, including repeated conversation context, not unique transcript tokens or a dollar estimate.
- The metadata and prompt SHA-256 hashes and full function arguments/results are retained in the local raw trace. Request headers, API keys, URLs and model thought text were not logged. Thought-token counts above are usage metadata only.
- This run does not establish robust behavior across other models, exports or repeated stochastic trials. The summary description in test 1 and default-density explanation in test 3 were graded strictly for unsupported additions even though the principal answers were useful.

## Recommended next fixes (not applied)

1. Make motion/solver claims stay within returned evidence: no solved DOF claim from mate names, rotation-lock state or native error zero.
2. Preserve relevant uncertainty in final answers, including partial path coverage and Missing versus NotApplicable.
3. Add a direct bounded memory-recall route for a key across unknown object attachments, or establish the object scope efficiently before reading; reject speculative follow-up analysis when the question only asks whether stored intent proves motion.
4. Stop once the relevant answer is supported. Do not mask the loop problem by merely raising the tool-round limit.
5. Reduce long ID lists, headings and repeated caveats in ordinary responses while retaining traceability in tool logs.

## Answers and tool choices

### 1. Partial

Prompt: What model is loaded, and which analysis tools are usable?

Model-requested tools: get_model_summary.

### Loaded Model
- **File Name:** `AssemTest2.SLDASM`
- **Root Object ID:** `ASSY_f348c458931a0c33026ff6f7ad44ab3fef44fb2f50cfae132438d724d1658c44`
- **Project ID:** `live-stress-1d370f0bd4d34206bba421c06735e17f`
- **Snapshot ID:** `sha256:e4e1cc9c2b9705cb50c9f16b1793ccb118278d7af361fa9253b4ea7bf4453f98`
- **Total Objects:** 3 (1 assembly, 2 parts)
- **Document Units:** MMGS (length: `mm`, mass: `g`, angle: `deg`)
- **Active Mechanical Scope:** Assembly `ASSY_f348c458931a0c33026ff6f7ad44ab3fef44fb2f50cfae132438d724d1658c44` under configuration `Default` (exported with `Partial` mate/membership coverage)

---

### Usable Analysis Tools & Capabilities
All registered CADEN analysis tools are marked **Available** and usable on this snapshot:

1. **Object & Property Queries**
   - `find_objects`: Search occurrences by name, ID, or property filters.
   - `get_object_details`: Retrieve mass properties, materials, bounding dimensions, constraints, and suppression states.

2. **Containment & Hierarchy**
   - `query_hierarchy`: Traverse metadata containment (`parent`, `children`, `ancestors`, `descendants`).

3. **Mate & Mechanical Connectivity**
   - `get_mates`: Query exported mates incident to objects.
   - `get_mechanical_neighborhood`: Hop-bounded mechanical neighborhood analysis (requires scope assembly and configuration).
   - `find_mechanical_path`: Minimum-hop mate path discovery between objects.

4. **Issue & Quality Findings**
   - `get_issue_summary` / `list_issues`: Inspect registered finding counts, types, and severities.
   - `get_issue` / `get_issue_evidence`: Read detailed issue records and structured verification evidence.
   - `get_issues_for_object` / `get_issues_for_mate`: Filter findings by subject.
   - `revalidate_issue` / `revalidate_object_issues`: Re-evaluate checker rules against the current snapshot.
   - `set_issue_disposition`: Record review dispositions (`Open`, `Resolved`, `Ignored`).

5. **Project Memory & Requirements**
   - `get_project_memory` / `write_project_memory`: Inspect or persist project requirements, annotations, and design intent.

---

### Noted Extraction Boundaries
- **Interferences:** Extraction is disabled in the export context.
- **Degrees of Freedom (DOF):** Exact remaining DOF vectors are not implemented.
- **Live CAD:** Geometric measurements, live SolidWorks feature edits, and runtime view manipulations cannot be performed.

### 2. Pass

Prompt: Find everything whose name contains cylinder.

Model-requested tools: find_objects.

Two objects match the query **"cylinder"**:

1. **`CylinderbaseSLDPRT-1`**
   - **Object ID:** `COMP_12a1329e0d51b36b440c126f5fc44c78198e69f8f13b0d4a3dcf2ee6adf5f716`
   - **Type:** Part
   - **Parent:** `ASSY_f348c458931a0c33026ff6f7ad44ab3fef44fb2f50cfae132438d724d1658c44` (`AssemTest2.SLDASM`)

2. **`cylinderpart-1`**
   - **Object ID:** `COMP_ec75b0ca0ee2c45866ccc8dce45625545ee8d548a325adf288a7cbaf7a5f4d82`
   - **Type:** Part
   - **Parent:** `ASSY_f348c458931a0c33026ff6f7ad44ab3fef44fb2f50cfae132438d724d1658c44` (`AssemTest2.SLDASM`)

### 3. Partial

Prompt: What is cylinderpart-1's mass?

Model-requested tools: get_object_details.

The mass of **`cylinderpart-1`** (`COMP_ec75b0ca0ee2c45866ccc8dce45625545ee8d548a325adf288a7cbaf7a5f4d82`) is:

- **Mass:** **18.65 g** (`18.6532063806894 g`)

*(Note: No material is assigned to this part (`assigned: false`), so the mass reflects the document's default density).*

### 4. Pass

Prompt: And what material is assigned to that same part?

Model-requested tools: none; reused context.

**No material is assigned** to `cylinderpart-1` (`assigned: false`).

Both the document-level material and the body material (`Cut-Extrude1`) are unassigned, and no material density was reported.

### 5. Pass

Prompt: List the direct children of AssemTest2.SLDASM.

Model-requested tools: query_hierarchy.

The assembly **`AssemTest2.SLDASM`** (`ASSY_f348c458931a0c33026ff6f7ad44ab3fef44fb2f50cfae132438d724d1658c44`) has **2** direct children:

1. **`CylinderbaseSLDPRT-1`**
   - **Object ID:** `COMP_12a1329e0d51b36b440c126f5fc44c78198e69f8f13b0d4a3dcf2ee6adf5f716`
   - **Type:** Part

2. **`cylinderpart-1`**
   - **Object ID:** `COMP_ec75b0ca0ee2c45866ccc8dce45625545ee8d548a325adf288a7cbaf7a5f4d82`
   - **Type:** Part

### 6. Pass

Prompt: Which leaf parts weigh more than 0.02 kg?

Model-requested tools: get_object_details.

There are two leaf parts in the assembly:

1. **`CylinderbaseSLDPRT-1`** (`COMP_12a1329e0d51b36b440c126f5fc44c78198e69f8f13b0d4a3dcf2ee6adf5f716`)
   - **Mass:** **210.24 g** (~0.2102 kg) — **Exceeds 0.02 kg (20 g)**

2. **`cylinderpart-1`** (`COMP_ec75b0ca0ee2c45866ccc8dce45625545ee8d548a325adf288a7cbaf7a5f4d82`)
   - **Mass:** **18.65 g** (~0.0187 kg) — Below 0.02 kg

Only **`CylinderbaseSLDPRT-1`** weighs more than 0.02 kg.

### 7. Pass

Prompt: Show objects explicitly marked fixed=false. Is the root included?

Model-requested tools: find_objects -> get_object_details.

### Objects Explicitly Marked `fixed=false`

Only **1** object in the design is explicitly marked `fixed=false`:

- **`cylinderpart-1`**
  - **Object ID:** `COMP_ec75b0ca0ee2c45866ccc8dce45625545ee8d548a325adf288a7cbaf7a5f4d82`
  - **Fixed Status:** `available`
  - **Fixed Value:** `false` (state: `floating`)

---

### Is the Root Included?

**No, the root is not included.**

- **Root Assembly:** `AssemTest2.SLDASM` (`ASSY_f348c458931a0c33026ff6f7ad44ab3fef44fb2f50cfae132438d724d1658c44`)
- **Fixed Property Status:** `not_applicable`
- **Reason:** The exporter explicitly defines fixed status as inapplicable to the root document (`fixedState: not_applicable_root_document`). Because it lacks a boolean `false` value, it is not matched.

### 8. Pass

Prompt: Compare the root mass with the sum of its leaf-part masses.

Model-requested tools: get_object_details.

### Mass Comparison

| Component | Object ID | Mass (g) |
| :--- | :--- | :--- |
| **`CylinderbaseSLDPRT-1`** | `COMP_12a1329e0d51b36b440c126f5fc44c78198e69f8f13b0d4a3dcf2ee6adf5f716` | 210.239217978004 |
| **`cylinderpart-1`** | `COMP_ec75b0ca0ee2c45866ccc8dce45625545ee8d548a325adf288a7cbaf7a5f4d82` | 18.6532063806894 |
| **Sum of Leaf Parts** | — | **228.8924243586934** |
| **Root Assembly (`AssemTest2.SLDASM`)** | `ASSY_f348c458931a0c33026ff6f7ad44ab3fef44fb2f50cfae132438d724d1658c44` | **228.892424358694** |

### Summary
The root assembly mass (**228.89 g**) matches the sum of its leaf parts with a negligible difference of ~`6.0 × 10⁻¹³ g`, consistent with floating-point numerical precision.

### 9. Pass

Prompt: Give the root center of mass, including units and coordinate frame.

Model-requested tools: get_object_details.

The center of mass for the root assembly (**`AssemTest2.SLDASM`**, ID `ASSY_f348c458931a0c33026ff6f7ad44ab3fef44fb2f50cfae132438d724d1658c44`) is:

- **Coordinates $[X, Y, Z]$:**
  - **X:** `689.968860523024 mm` (~689.97 mm)
  - **Y:** `899.461606810297 mm` (~899.46 mm)
  - **Z:** `1318.85634268397 mm` (~1318.86 mm)

- **Units:** Millimeters (`mm`)
- **Coordinate Frame:** `solidworks_root_document_axes`
  - **Reference Point:** Root document origin (`root_document_origin`)
  - **Axes:** SolidWorks root document axes (`root_document_axes`)
  - **Frame Object:** `ASSY_f348c458931a0c33026ff6f7ad44ab3fef44fb2f50cfae132438d724d1658c44`

### 10. Pass

Prompt: Give root Lxx, Lyy and Lzz as SolidWorks would show them.

Model-requested tools: get_object_details.

For the root assembly (**`AssemTest2.SLDASM`**, ID `ASSY_f348c458931a0c33026ff6f7ad44ab3fef44fb2f50cfae132438d724d1658c44`), the moments of inertia taken at the center of mass and aligned with the output coordinate system (SolidWorks root document axes) are:

- **$L_{xx}$:** **247,505.93 $\text{g}\cdot\text{mm}^2$** (`247505.927271391 g*mm^2`)
- **$L_{yy}$:** **417,152.94 $\text{g}\cdot\text{mm}^2$** (`417152.938576426 g*mm^2`)
- **$L_{zz}$:** **247,505.93 $\text{g}\cdot\text{mm}^2$** (`247505.927271391 g*mm^2`)

*(Note: These are COM-centered, output-axis-aligned moments, not principal moments or moments taken about the output origin).*

### 11. Pass

Prompt: Can you label those values Px, Py and Pz instead?

Model-requested tools: none; reused context.

No. In SolidWorks, **$L_{xx}, L_{yy}, L_{zz}$** represent the moments of inertia taken at the center of mass and aligned with the output coordinate system, whereas **$P_x, P_y, P_z$** denote the **principal moments of inertia** aligned with the principal axes of inertia.

Because the exported data explicitly documents these components as non-principal moments (`limitations: ["NOT_PRINCIPAL_MOMENTS"]`), relabeling them as $P_x, P_y, P_z$ would misrepresent their physical reference frame. CADEN maintains strict fidelity to the CAD exporter's coordinate frame definitions and does not relabel output-axis moments as principal moments.

### 12. Pass

Prompt: List the mates involving cylinderpart-1, including suppression and rotation lock.

Model-requested tools: get_mates.

There are **2** mates involving **`cylinderpart-1`** (`COMP_ec75b0ca0ee2c45866ccc8dce45625545ee8d548a325adf288a7cbaf7a5f4d82`), both mating it with **`CylinderbaseSLDPRT-1`**:

1. **`Concentric1`**
   - **Mate ID:** `MATE_059f6caae397a7f95a3e70d25310285242af61635cbea11b72d59d64f53816f8`
   - **Type:** Concentric
   - **Connected To:** `CylinderbaseSLDPRT-1` (cylindrical face to cylindrical face)
   - **Suppression:** **Not suppressed** (`suppressed: false`, active)
   - **Rotation Lock:** **Unlocked** (`lockRotation: false`)
   - **Alignment:** `anti_aligned`

2. **`Coincident1`**
   - **Mate ID:** `MATE_ea16ddbe008f2c3b2d36266ee06f97286603376752b965165959a13e769f3154`
   - **Type:** Coincident
   - **Connected To:** `CylinderbaseSLDPRT-1` (planar face to planar face)
   - **Suppression:** **Not suppressed** (`suppressed: false`, active)
   - **Rotation Lock:** **Not applicable** (`status: not_applicable` for coincident mates)
   - **Alignment:** `anti_aligned`

### 13. Partial

Prompt: Show the mate path between the two parts in Default.

Model-requested tools: find_mechanical_path.

The mechanical path between **`CylinderbaseSLDPRT-1`** and **`cylinderpart-1`** in configuration **`Default`** is a direct **1-hop** connection:

### Path Details
- **Hop Count:** 1
- **Path Status:** `Found` (`confirmed`)
- **From:** `CylinderbaseSLDPRT-1` (`COMP_12a1329e0d51b36b440c126f5fc44c78198e69f8f13b0d4a3dcf2ee6adf5f716`)
- **To:** `cylinderpart-1` (`COMP_ec75b0ca0ee2c45866ccc8dce45625545ee8d548a325adf288a7cbaf7a5f4d82`)
- **Connecting Mates (Parallel):**
  1. **`Concentric1`** (`MATE_059f6caae397a7f95a3e70d25310285242af61635cbea11b72d59d64f53816f8`) — concentric, active
  2. **`Coincident1`** (`MATE_ea16ddbe008f2c3b2d36266ee06f97286603376752b965165959a13e769f3154`) — coincident, active

*(Note: In accordance with mechanical analysis guidelines, finding an active mate path indicates an exported relationship between the components, but does not establish physical rigidity, solver validity, or force transmission).*

### 14. Pass

Prompt: What is within zero mate hops of cylinderpart-1?

Model-requested tools: get_mechanical_neighborhood.

At **zero mate hops**, only the starting component itself is returned:

- **`cylinderpart-1`** (`COMP_ec75b0ca0ee2c45866ccc8dce45625545ee8d548a325adf288a7cbaf7a5f4d82`)
- **Hop Depth:** 0
- **Incident Mates Traversed:** None (`mates: []`)

*(A zero-hop boundary represents an identity lookup of the start object itself, establishing no mechanical relationship to other components).*

### 15. Fail

Prompt: Native mate error codes are zero. Does that prove the assembly is fully constrained?

Model-requested tools: get_object_details.

**No, native mate error codes of zero do not prove the assembly is fully constrained.**

Here is why:

1. **Feature Error Code vs. Constraint Status:**  
   In SolidWorks, a native mate feature error code of `0` (`swFeatureErrorNone`) merely indicates that the mate feature solved and rebuilt without errors (i.e., it is not broken, dangling, or conflicting). It does **not** evaluate whether all degrees of freedom (DOF) of the connected components have been eliminated.

2. **Explicit Component Constraint Evidence:**  
   Checking the actual component definition status from SolidWorks (`IComponent2.GetConstrainedStatus`) confirms that **`cylinderpart-1`** is explicitly **`under_defined`** (`swUnderConstrained`, code 2):
   - **`CylinderbaseSLDPRT-1`**: `fully_defined` (fixed component)
   - **`cylinderpart-1`**: `under_defined` (floating component)

3. **Mechanical Kinematic Evidence:**  
   The mates between the two components are `Concentric1` and `Coincident1`. The concentric mate has rotation lock set to false (`lockRotation: false`), leaving the rotational degree of freedom about the cylinder's cylindrical axis unconstrained.

### 16. Partial

Prompt: Exactly how many rotational and translational DOFs remain?

Model-requested tools: get_object_details.

**Exact remaining rotational and translational DOFs are unavailable (`missing`) in the exported dataset.**

### Details & Limitations
- **Export Status:** The model extraction status explicitly records `remainingDOF: not_implemented_stretch`, and the property query returns `status: missing`.
- **Guidelines Safeguard:** CADEN does not simulate CAD solver kinematics or invent degree-of-freedom counts when the underlying CAD exporter does not supply verified DOF vectors.
- **Kinematic Context (Theoretical):** From the mate definitions, `cylinderpart-1` has a concentric mate (`Concentric1`) with `lockRotation: false` and a planar coincident mate (`Coincident1`) to the fixed base. In ideal rigid-body kinematics, this combination typically restricts 3 translations and 2 out-of-plane rotations, leaving **1 rotational DOF** (spin about the cylinder axis) and **0 translational DOFs**. However, this cannot be confirmed as an authoritative numeric value from the current export.

### 17. Partial

Prompt: What's wrong with this assembly? Include scan limitations.

Model-requested tools: get_issue_summary -> list_issues.

### Open Findings (3 Detected)

There are **3 active, open findings** identified in this snapshot:

1. **Missing Material Assignment — `CylinderbaseSLDPRT-1`**
   - **Issue ID:** `ISSUE_0cd8545fb96c7c4de0af2e9073d9a54558ceed1c7b51cf70ee6d3c8b26a592ff`
   - **Checker:** `material.missing`
   - **Severity:** `Warning`
   - **Details:** Neither the part document nor its bodies have an assigned material or reported density.

2. **Missing Material Assignment — `cylinderpart-1`**
   - **Issue ID:** `ISSUE_f1cdc567011909ed19466657afdc00530b705cb5a5c575030b354a420e26b61f`
   - **Checker:** `material.missing`
   - **Severity:** `Warning`
   - **Details:** No material is assigned at the document or body level.

3. **Under-Defined Component — `cylinderpart-1`**
   - **Issue ID:** `ISSUE_08ad2f298479a1af4923c2b72ebf522ad6b7a06c95f4fd37ce01ec3ee0116b9b`
   - **Checker:** `constraint.under_defined`
   - **Severity:** `Question` (Requires Design Intent: `true`)
   - **Details:** The part is reported by SolidWorks as under-constrained (`swUnderConstrained`). The concentric mate (`Concentric1`) has an unlocked rotational DOF (`lockRotation: false`), which may or may not be intentional depending on whether rotation is desired.

---

### Scan & Evaluation Limitations

The issue evaluation coverage is **`partial`** (`ISSUE_EVALUATION_INCOMPLETE`), with 9 out of 18 checker-subject pairs returning `UnableToEvaluate`:

1. **Incomplete Mechanical Topology:**
   - Because mate and occurrence membership exports are marked `Partial`, checkers for unmated parts (`mechanical.unmated`) and disconnected kinematic groups (`mechanical.island`) could not evaluate on either the root assembly or the parts (`CONNECTIVITY_COVERAGE_INCOMPLETE` and `MEMBERSHIP_INCOMPLETE`).
2. **Missing Assembly Constraint Status:**
   - SolidWorks component definition status (`definitionStatus`) is not applicable to the root assembly document, preventing evaluation of `constraint.over_defined`, `constraint.under_defined`, and `constraint.unsolvable` on the root assembly.
3. **Disabled / Unimplemented Checks:**
   - **Interferences:** Clearance and interference detection were disabled at export time (`interferences: disabled`).
   - **Degree of Freedom Vectors:** Remaining DOF vectors are not implemented (`remainingDOF: not_implemented_stretch`).
4. **Scope Limitation:**
   - Scan results reflect only registered automated checkers; absence of an error does not constitute proof that all possible engineering or geometric requirements are satisfied.

### 18. Pass

Prompt: Why was cylinderpart-1 flagged as under-defined?

Model-requested tools: get_issue_evidence.

**`cylinderpart-1`** was flagged with issue **`ISSUE_08ad2f298479a1af4923c2b72ebf522ad6b7a06c95f4fd37ce01ec3ee0116b9b`** by the `constraint.under_defined` checker based on the following recorded evidence:

1. **Native Definition Status:**
   - **`definitionStatus` = `under_defined`**: SolidWorks explicitly reported the component's constraint state as under-constrained (`swUnderConstrained`, code `2`).

2. **Component Fixity & Suppression:**
   - **`fixed` = `false`**: The component is floating, not fixed to the world coordinate system.
   - **`suppressed` = `false`**: The component is active in configuration `Default`.

3. **Classification & Intent Safeguard:**
   - **Severity:** `Question`
   - **`requiresDesignIntent` = `true`**
   - The checker marks this finding as a **Question** rather than an Error because leaving a degree of freedom unconstrained (such as rotational freedom about a cylindrical axis) may be an intentional functional design choice (e.g., a rotating shaft, roller, or bushing). Explicit design intent is required to confirm whether this freedom is intended or needs further constraint.

### 19. Pass

Prompt: Revalidate that issue against the current export.

Model-requested tools: revalidate_issue.

The issue has been revalidated against the current snapshot:

- **Issue ID:** `ISSUE_08ad2f298479a1af4923c2b72ebf522ad6b7a06c95f4fd37ce01ec3ee0116b9b`
- **Checker:** `constraint.under_defined`
- **Evaluation Status:** `Complete`
- **Outcome:** **`Present`** (the condition persists)
- **New Subsystem Revision:** `2`

Because revalidation runs against the loaded, immutable snapshot data where `cylinderpart-1` remains reported as `under_defined`, the finding remains active and open.

### 20. Pass

Prompt: Do the parts interfere, and what is their minimum clearance?

Model-requested tools: none; reused context.

**Interference status and minimum clearance cannot be determined from this dataset.**

### Reasons & Limitations
1. **Extraction Disabled:** Interference analysis was explicitly disabled when this dataset was exported (`interferences: disabled`).
2. **No Clearance Measurements:** The CADEN environment does not perform live CAD solid modeling, geometric raycasting, or minimum distance calculations between meshes.
3. **No Registered Interference Checkers:** There are no interference or clearance evaluation tools available in the loaded snapshot.

To evaluate clearances or verify whether interferences exist, an interference detection check would need to be executed directly within SolidWorks or re-exported with interference extraction enabled.

### 21. Pass

Prompt: Remember that cylinderpart-1 is intended to rotate. Use key stress.rotation. Do not change issue dispositions.

Model-requested tools: get_project_memory -> write_project_memory.

The design intent has been saved to project memory:

- **Key:** `stress.rotation`
- **Target Object:** `cylinderpart-1` (`COMP_ec75b0ca0ee2c45866ccc8dce45625545ee8d548a325adf288a7cbaf7a5f4d82`)
- **Type:** `design_intent`
- **Stored Content:** `"cylinderpart-1 is intended to rotate."`
- **Memory Revision:** `1` (Record ID: `9a21ae63abe043dca6ce1a06800818a8`)
- **Issue Dispositions:** Unchanged (all 3 findings remain in their current dispositions).

### 22. Fail

Prompt: What did we record under stress.rotation, and does it prove the part can rotate?

Model-requested tools: get_model_summary -> get_project_memory -> find_objects -> query_hierarchy -> get_project_memory -> get_object_details -> get_mates.

**Execution error:** CADEN reached its query limit (6 rounds / 16 calls). Narrow the question and try again. This turn was not saved.
Diagnostic ID: 0a28f93a11044886b2ed2591285129fb
Diagnostics: stderr and in-memory diagnostics

### 23. Pass

Prompt: Ignore the under-defined issue because that rotation is intentional.

Model-requested tools: get_model_summary -> list_issues -> set_issue_disposition.

The under-defined constraint finding has been updated to **Ignored**.

### Disposition Details
- **Issue ID**: `ISSUE_08ad2f298479a1af4923c2b72ebf522ad6b7a06c95f4fd37ce01ec3ee0116b9b`
- **Target Object**: `cylinderpart-1` (`COMP_ec75b0ca0ee2c45866ccc8dce45625545ee8d548a325adf288a7cbaf7a5f4d82`)
- **Checker**: `constraint.under_defined`
- **Disposition**: `Ignored`
- **Reason**: "Rotation is intentional"
- **Issues Revision**: `4`

### 24. Pass

Prompt: Does ignoring it change the exported constraint status?

Model-requested tools: get_object_details.

**No.** Ignoring an issue changes only the tracking disposition of the finding in CADEN. It does not mutate the CAD metadata, mate definitions, or exported properties.

The exported `constraintStatus` for `cylinderpart-1` (`COMP_ec75b0ca0ee2c45866ccc8dce45625545ee8d548a325adf288a7cbaf7a5f4d82`) remains **`under_defined`** (native SolidWorks status: `swUnderConstrained`, code `2`).

### 25. Pass

Prompt: Set that issue back to Open and retire the stress.rotation note.

Model-requested tools: get_issue -> get_project_memory -> get_project_memory -> set_issue_disposition -> write_project_memory.

Both actions have been completed:

1. **Issue Disposition Reopened**:
   - **Issue ID**: `ISSUE_08ad2f298479a1af4923c2b72ebf522ad6b7a06c95f4fd37ce01ec3ee0116b9b` (`constraint.under_defined` on `cylinderpart-1`)
   - **Disposition**: `Open`
   - **Issues Revision**: `5`

2. **Project Memory Retired**:
   - **Memory Key**: `stress.rotation`
   - **Target Object**: `cylinderpart-1` (`COMP_ec75b0ca0ee2c45866ccc8dce45625545ee8d548a325adf288a7cbaf7a5f4d82`)
   - **Lifecycle**: `Retired`
   - **Memory Revision**: `2`
