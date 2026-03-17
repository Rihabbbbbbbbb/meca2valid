# ValidateTemplate Azure Function API Documentation

## Overview
The ValidateTemplate API endpoint validates user-provided templates against predefined blueprint specifications and generates comprehensive validation reports.

## Endpoint Information

**Base URL**: `https://validspecleon-c3gtcqege5g8etdp.francecentral-01.azurewebsites.net`

**Endpoint**: `/api/validatetemplate`

**Method**: `POST`

**Authentication**: Function Key (API Key)

---

## Authentication

Add the function key as a query parameter:
```
?code=YOUR_FUNCTION_KEY_HERE
```

**Complete URL**:
```
https://validspecleon-c3gtcqege5g8etdp.francecentral-01.azurewebsites.net/api/validatetemplate?code=YOUR_FUNCTION_KEY_HERE
```

---

## Request

### Headers
```
Content-Type: application/json
```

### Request Body Schema

```json
{
  "templateBlueprint": "string (required)",
  "user": "string (required)",
  "out": "string (optional)"
}
```

#### Fields

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `templateBlueprint` | string | ✅ Yes | The blueprint template identifier or path |
| `user` | string | ✅ Yes | The user identifier requesting validation |
| `out` | string | ❌ No | Optional output path for the validation report |

### Example Request

```bash
curl -X POST "https://validspecleon-c3gtcqege5g8etdp.francecentral-01.azurewebsites.net/api/validatetemplate?code=YOUR_FUNCTION_KEY_HERE" \
  -H "Content-Type: application/json" \
  -d '{
    "templateBlueprint": "my-template",
    "user": "john.doe@example.com"
  }'
```

---

## Response

### Success Response (HTTP 200)

```json
{
  "message": "Validation report created successfully",
  "reportPath": "D:\\home\\site\\wwwroot\\validation-report.json"
}
```

### Error Responses

#### Bad Request (HTTP 400)

**Missing Required Fields**:
```json
{
  "error": "Invalid request body. Required fields: templateBlueprint, user",
  "details": "Both 'templateBlueprint' and 'user' fields are mandatory"
}
```

**Invalid JSON Format**:
```json
{
  "error": "Invalid JSON format in request body",
  "details": "Unexpected character encountered..."
}
```

#### Internal Server Error (HTTP 500)

```json
{
  "error": "An error occurred during validation",
  "details": "Error message details"
}
```

---

## Status Codes

| Code | Description |
|------|-------------|
| 200 | Validation successful |
| 400 | Bad request (missing fields or invalid JSON) |
| 401 | Unauthorized (invalid or missing function key) |
| 500 | Internal server error |

---

## Examples

### Example 1: Successful Validation

**Request**:
```json
{
  "templateBlueprint": "standard-template-v1",
  "user": "alice@stellantis.com"
}
```

**Response** (200 OK):
```json
{
  "message": "Validation report created successfully",
  "reportPath": "D:\\home\\site\\wwwroot\\validation-report.json"
}
```

---

### Example 2: Custom Output Path

**Request**:
```json
{
  "templateBlueprint": "custom-template",
  "user": "bob@stellantis.com",
  "out": "/reports/custom-validation.json"
}
```

**Response** (200 OK):
```json
{
  "message": "Validation report created successfully",
  "reportPath": "/reports/custom-validation.json"
}
```

---

### Example 3: Missing Required Field

**Request**:
```json
{
  "user": "charlie@stellantis.com"
}
```

**Response** (400 Bad Request):
```json
{
  "error": "Invalid request body. Required fields: templateBlueprint, user",
  "details": "Both 'templateBlueprint' and 'user' fields are mandatory"
}
```

---

## Integration Examples

### PowerShell

```powershell
$uri = "https://validspecleon-c3gtcqege5g8etdp.francecentral-01.azurewebsites.net/api/validatetemplate?code=YOUR_FUNCTION_KEY_HERE"

$body = @{
    templateBlueprint = "my-template"
    user = "user@example.com"
} | ConvertTo-Json

$response = Invoke-RestMethod -Uri $uri -Method Post -Body $body -ContentType "application/json"
Write-Output $response
```

### Python

```python
import requests
import json

url = "https://validspecleon-c3gtcqege5g8etdp.francecentral-01.azurewebsites.net/api/validatetemplate"
params = {"code": "YOUR_FUNCTION_KEY_HERE"}
headers = {"Content-Type": "application/json"}
data = {
    "templateBlueprint": "my-template",
    "user": "user@example.com"
}

response = requests.post(url, params=params, headers=headers, json=data)
print(response.json())
```

### JavaScript / Node.js

```javascript
const axios = require('axios');

const url = 'https://validspecleon-c3gtcqege5g8etdp.francecentral-01.azurewebsites.net/api/validatetemplate';
const params = { code: 'YOUR_FUNCTION_KEY_HERE' };
const data = {
  templateBlueprint: 'my-template',
  user: 'user@example.com'
};

axios.post(url, data, { params })
  .then(response => console.log(response.data))
  .catch(error => console.error(error));
```

### C#

```csharp
using System.Net.Http;
using System.Text;
using System.Text.Json;

var client = new HttpClient();
var url = "https://validspecleon-c3gtcqege5g8etdp.francecentral-01.azurewebsites.net/api/validatetemplate?code=YOUR_FUNCTION_KEY_HERE";

var requestData = new
{
    templateBlueprint = "my-template",
    user = "user@example.com"
};

var json = JsonSerializer.Serialize(requestData);
var content = new StringContent(json, Encoding.UTF8, "application/json");

var response = await client.PostAsync(url, content);
var result = await response.Content.ReadAsStringAsync();
Console.WriteLine(result);
```

---

## Swagger / OpenAPI Documentation

Access the interactive Swagger UI at:
```
https://validspecleon-c3gtcqege5g8etdp.francecentral-01.azurewebsites.net/api/swagger/ui
```

Download the OpenAPI specification (JSON):
```
https://validspecleon-c3gtcqege5g8etdp.francecentral-01.azurewebsites.net/api/swagger.json
```

---

## Monitoring and Logs

View real-time logs using Azure CLI:
```bash
az functionapp log tail --resource-group MLWorkloadsRG --name ValidSpecLeon
```

Access Application Insights in Azure Portal for detailed telemetry and performance metrics.

---

## Support

For issues or questions, please contact the development team or create an issue in the project repository.

---

## Version History

| Version | Date | Changes |
|---------|------|---------|
| 1.0.0 | 2026-03-16 | Initial release with OpenAPI support |
