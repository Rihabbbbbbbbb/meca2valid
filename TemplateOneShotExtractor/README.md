# TemplateOneShotExtractor

Standalone one-shot tool for template-only extraction.

## What it does
- Reads one template DOCX.
- Extracts heading titles in strict order.
- Detects heading levels.
- Produces deep structure diagnostics:
  - duplicate canonical titles
  - level jump anomalies
  - numbering consistency issues
  - empty/weak content sections

## Output
Generates a JSON blueprint with:
- orderedTitleList: strict sequence of titles
- headings: heading metadata and content stats
- tableInventory: extracted DOCX tables linked to sections (row/column counts, header signatures, placeholder/red-text signals)
- quality: structural diagnostics
- stats: aggregate counters

Validation mode generates `validation-report.json` with:
- contract: required pivot titles (stable structure)
- scores: pivot coverage, subtitle recall/precision, order score, final score
- findings: missing pivots, extra pivots, per-pivot subtitle gaps
- tableValidation (advisory): template-vs-user table section coverage and header signature matching quality
- decision: PASS / WARN / FAIL with reasons
- quality gates (P06):
  - red text detection evidence (`RedTextEvidence`)
  - empty section violations without `Not applicable` / `Sans objet`
- semantic mapping summary:
  - applied alias/merge rules for renamed pivots
  - mapping events used during decision
- policy compliance summary:
  - profile used (`Must` / `Should` / `Optional` canonical sections)
  - missing/empty `Must` sections (hard fail)
  - missing/empty `Should` sections (advisory)
- explainability:
  - confidence band (`HIGH` / `MEDIUM` / `LOW`)
  - explainability notes with score drivers

## Professional test steps (validated)

1) Build only

From workspace root:

powershell
dotnet build .\TemplateOneShotExtractor\TemplateOneShotExtractor.csproj -c Release -v minimal

Expected: build succeeds (exit code 0).

2) Run successful extraction

powershell
dotnet run --project .\TemplateOneShotExtractor\TemplateOneShotExtractor.csproj -c Release -- --template "C:\Users\TA29225\Desktop\Component_or_Part_Specification_Template.docx" --out ".\TemplateOneShotExtractor\template-blueprint.json"

Expected: exit code 0 and file created:
- .\TemplateOneShotExtractor\template-blueprint.json

3) Verify generated result quickly

powershell
Get-Content .\TemplateOneShotExtractor\template-blueprint.json -TotalCount 40

Check these fields in JSON:
- Metadata.TemplatePath
- Stats.TotalHeadings
- Stats.TopLevelHeadings
- OrderedTitleList

4) Negative test: no arguments

powershell
dotnet run --project .\TemplateOneShotExtractor\TemplateOneShotExtractor.csproj -c Release --

Expected: exit code 2.

5) Negative test: missing file

powershell
dotnet run --project .\TemplateOneShotExtractor\TemplateOneShotExtractor.csproj -c Release -- --template "C:\does-not-exist\x.docx"

Expected: exit code 3.

## Contract-based validation (template stable, subtitles flexible)

1) Build template blueprint once

powershell
dotnet run --project .\TemplateOneShotExtractor\TemplateOneShotExtractor.csproj -c Release -- --template "C:\Users\TA29225\Desktop\Component_or_Part_Specification_Template.docx" --out ".\TemplateOneShotExtractor\template-blueprint.json"

2) Validate any user DOCX against template contract

powershell
dotnet run --project .\TemplateOneShotExtractor\TemplateOneShotExtractor.csproj -c Release -- --validate --templateBlueprint ".\TemplateOneShotExtractor\template-blueprint.json" --user "C:\path\user.docx" --out ".\TemplateOneShotExtractor\validation-report.json" --pivotLevel 2

Optional custom semantic mapping file:

powershell
dotnet run --project .\TemplateOneShotExtractor\TemplateOneShotExtractor.csproj -c Release -- --validate --templateBlueprint ".\TemplateOneShotExtractor\template-blueprint.json" --user "C:\path\user.docx" --mapping ".\TemplateOneShotExtractor\semantic-mapping.json" --out ".\TemplateOneShotExtractor\validation-report.json" --pivotLevel 2

Optional custom validation policy file:

powershell
dotnet run --project .\TemplateOneShotExtractor\TemplateOneShotExtractor.csproj -c Release -- --validate --templateBlueprint ".\TemplateOneShotExtractor\template-blueprint.json" --user "C:\path\user.docx" --policy ".\TemplateOneShotExtractor\template-policy.json" --out ".\TemplateOneShotExtractor\validation-report.json" --pivotLevel 2

Notes:
- If `--policy` is omitted, the tool auto-loads `template-policy.json` from current working directory, then from `TemplateOneShotExtractor\template-policy.json` if present.
- If no policy file is found, policy mode remains open (no Must/Should enforcement).

Exit codes:
- 0: validation executed, verdict PASS or WARN
- 6: validation executed, verdict FAIL (required pivot missing)

3) One-command wrapper (CMD)

powershell
cmd /c "C:\User\TA29225\WordProcessor\run-template-validate.cmd" "C:\User\TA29225\WordProcessor\TemplateOneShotExtractor\template-blueprint.json" "C:\path\user.docx"

## Script mode (optional)

Shortcut script:

powershell
.\run-template-blueprint.ps1 -Template "C:\path\Template.docx" -Out ".\TemplateOneShotExtractor\my-blueprint.json"

If your machine blocks scripts (ExecutionPolicy), use direct dotnet commands above.

