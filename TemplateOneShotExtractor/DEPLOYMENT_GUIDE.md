# Deployment Guide - ValidateTemplate Azure Function

## 📦 Package Information

- **Package Name**: `TemplateOneShotExtractor.zip`
- **Package Size**: ~5.0 MB
- **Location**: `C:\Users\TA29225\V2_Leon_github\CDC_Stellantis\TemplateOneShotExtractor\`
- **Build Date**: 2026-03-16
- **Runtime**: .NET 8 Isolated Worker
- **Azure Functions Version**: v4

---

## 🚀 Deployment Instructions

### Method 1: Azure Cloud Shell (Recommended)

1. **Upload the ZIP to Cloud Shell**:
   - Open Azure Cloud Shell: https://shell.azure.com
   - Click the upload button (⬆️)
   - Select `TemplateOneShotExtractor.zip`
   - Wait for upload to complete

2. **Deploy using Azure CLI**:
```bash
az functionapp deployment source config-zip --resource-group MLWorkloadsRG --name ValidSpecLeon --src TemplateOneShotExtractor.zip
```

3. **Restart the Function App**:
```bash
az functionapp restart --resource-group MLWorkloadsRG --name ValidSpecLeon
```

4. **Wait 2-3 minutes** for the restart to complete.

5. **Verify deployment**:
```bash
az functionapp function list --resource-group MLWorkloadsRG --name ValidSpecLeon --output table
```

---

### Method 2: Azure Portal (Kudu)

1. Go to: https://portal.azure.com
2. Navigate to: **Function Apps** → **ValidSpecLeon**
3. Click: **Advanced Tools** → **Go** (opens Kudu)
4. In Kudu: **Tools** → **Zip Push Deploy**
5. Drag and drop: `TemplateOneShotExtractor.zip`
6. Wait for extraction to complete
7. Return to Azure Portal and restart the Function App

---

### Method 3: VS Code Azure Extension

1. Install: **Azure Functions extension** in VS Code
2. Sign in to Azure
3. Right-click: `publish` folder
4. Select: **Deploy to Function App...**
5. Choose: **ValidSpecLeon**
6. Confirm deployment

---

## ✅ Post-Deployment Verification

### 1. Verify Function is Running

```bash
az functionapp show --resource-group MLWorkloadsRG --name ValidSpecLeon --query "state" --output tsv
```

**Expected**: `Running`

### 2. Check Function Discovery

```bash
az functionapp function list --resource-group MLWorkloadsRG --name ValidSpecLeon --output table
```

**Expected**: `ValidateTemplate` appears in the list

### 3. Access Swagger UI

**OpenAPI URL**: `https://validspecleon-c3gtcqege5g8etdp.francecentral-01.azurewebsites.net/api/swagger/ui`

**Swagger JSON**: `https://validspecleon-c3gtcqege5g8etdp.francecentral-01.azurewebsites.net/api/swagger.json`

### 4. Test the Endpoint

```bash
curl -X POST "https://validspecleon-c3gtcqege5g8etdp.francecentral-01.azurewebsites.net/api/validatetemplate?code=YOUR_FUNCTION_KEY_HERE" \
  -H "Content-Type: application/json" \
  -d '{"templateBlueprint": "test", "user": "testuser"}'
```

**Expected Response** (HTTP 200):
```json
{
  "message": "Validation report created successfully",
  "reportPath": "D:\\home\\site\\wwwroot\\validation-report.json"
}
```

---

## 🔍 Troubleshooting

### Issue: Function not appearing

**Solution**:
```bash
az functionapp restart --resource-group MLWorkloadsRG --name ValidSpecLeon
# Wait 3 minutes
az functionapp function list --resource-group MLWorkloadsRG --name ValidSpecLeon --output table
```

### Issue: Swagger UI not loading

**Check**:
1. Verify OpenAPI package is installed: `Microsoft.Azure.Functions.Worker.Extensions.OpenApi`
2. Ensure `.ConfigureOpenApi()` is in `Program.cs`
3. Restart the Function App

**Logs**:
```bash
az functionapp log tail --resource-group MLWorkloadsRG --name ValidSpecLeon
```

### Issue: 500 Internal Server Error

**Check Application Insights**:
1. Azure Portal → ValidSpecLeon → Application Insights
2. Check Exception logs
3. Review recent traces

**Stream logs**:
```bash
az functionapp log tail --resource-group MLWorkloadsRG --name ValidSpecLeon --filter Error
```

### Issue: Deployment fails

**Check deployment logs**:
```bash
az functionapp log deployment show --resource-group MLWorkloadsRG --name ValidSpecLeon
```

**Retry deployment**:
```bash
az functionapp deployment source config-zip --resource-group MLWorkloadsRG --name ValidSpecLeon --src TemplateOneShotExtractor.zip
```

---

## 📊 Configuration Settings

### Required Application Settings

| Setting | Value |
|---------|-------|
| `FUNCTIONS_WORKER_RUNTIME` | `dotnet-isolated` |
| `FUNCTIONS_EXTENSION_VERSION` | `~4` |
| `AzureWebJobsStorage` | `<storage-connection-string>` |
| `BLUEPRINT_PATH` | `https://spectemplatestorage.blob.core.windows.net/blueprints/template-blueprint.json` |

### Verify Settings

```bash
az functionapp config appsettings list --resource-group MLWorkloadsRG --name ValidSpecLeon --output table
```

---

## 🔒 Security

### Function Key

- **Default Key**: `YOUR_FUNCTION_KEY_HERE`
- **Location**: Query parameter `?code=<key>`

### Rotate Keys (if needed)

```bash
az functionapp keys set --resource-group MLWorkloadsRG --name ValidSpecLeon --key-name default --key-value "<new-key>"
```

### Get All Keys

```bash
az functionapp keys list --resource-group MLWorkloadsRG --name ValidSpecLeon
```

---

## 📈 Monitoring

### Real-time Logs

```bash
az functionapp log tail --resource-group MLWorkloadsRG --name ValidSpecLeon
```

### Application Insights

**Access**: Azure Portal → ValidSpecLeon → Application Insights

**Key Metrics**:
- Request count
- Response time
- Failure rate
- Dependency calls

### Alerts (Optional)

Create alerts for:
- Function failures > 5% in 5 minutes
- Response time > 5 seconds
- No requests in 1 hour (availability check)

---

## 🔄 Rollback Plan

If deployment causes issues:

1. **Stop the Function App**:
```bash
az functionapp stop --resource-group MLWorkloadsRG --name ValidSpecLeon
```

2. **Deploy previous version** (if available)

3. **Restart**:
```bash
az functionapp start --resource-group MLWorkloadsRG --name ValidSpecLeon
```

---

## 📝 Deployment Checklist

- [ ] Code reviewed and tested locally
- [ ] OpenAPI annotations added
- [ ] Build successful (`dotnet build`)
- [ ] Publish successful (`dotnet publish`)
- [ ] `host.json` copied to publish folder
- [ ] ZIP package created (~5 MB)
- [ ] ZIP uploaded to Cloud Shell / Azure Portal
- [ ] Deployment command executed
- [ ] Function App restarted
- [ ] Wait 2-3 minutes for restart
- [ ] Function appears in Functions list
- [ ] Swagger UI accessible
- [ ] Test endpoint returns HTTP 200
- [ ] Application Insights logs visible
- [ ] API documentation updated

---

## 📚 Related Documentation

- [API_DOCUMENTATION.md](./API_DOCUMENTATION.md) - Complete API reference
- [README.md](./README.md) - Project overview
- [Azure Functions Documentation](https://docs.microsoft.com/azure/azure-functions/)

---

## 🎯 Next Steps After Deployment

1. **Test all scenarios**:
   - Valid request (HTTP 200)
   - Missing fields (HTTP 400)
   - Invalid JSON (HTTP 400)

2. **Share API documentation** with team

3. **Integrate with Copilot Studio** (if needed)

4. **Set up monitoring alerts**

5. **Configure CI/CD pipeline** (optional)

---

## ✅ Deployment Success Criteria

✅ Function appears in Azure Portal  
✅ Swagger UI accessible  
✅ Test requests return expected responses  
✅ Logs show successful execution  
✅ No errors in Application Insights  
✅ Response time < 2 seconds  

---

**Last Updated**: 2026-03-16
