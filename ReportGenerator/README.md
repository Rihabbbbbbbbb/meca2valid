# ReportGenerator

Small console app to generate a human-friendly HTML report from a `validation-report-final.json` produced by TemplateOneShotExtractor.

Usage:

```
dotnet run --project ReportGenerator -- TemplateOneShotExtractor/validation-report-final.json ReportGenerator/output/validation-report.html
```

The tool writes `validation-report.html` and a `style.css` file next to it.
