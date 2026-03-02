Prerequisites

Autodesk APS Account
Create an APS app and get Client ID / Client Secret from Autodesk Developer portal.
.NET SDK
Install .NET 7/8 SDK (whichever you used in the project).
(Optional) Visual Studio / VS Code



Project Structure (example)
Controllers/ → Backend REST APIs (AuthController, DAController, OssController, etc.)
wwwroot/ → Frontend (HTML/JS/CSS)
Services/ → Auth + helper services
appsettings.json → APS config



Setup
1) Configure APS Client ID/Secret
✅ Recommended: keep secrets outside GitHub.
Create appsettings.Local.json
{
  "APS": {
    "ClientId": "YOUR_CLIENT_ID",
    "ClientSecret": "YOUR_CLIENT_SECRET",
    "CallbackUrl": "http://localhost:5000/api/auth/callback"
  }
}

Make sure .gitignore contains:
appsettings.Local.json
appsettings.Development.json
If your project uses Environment Variables instead, set:
APS__ClientId
APS__ClientSecret

Run Locally

From the project root (where .csproj exists):

dotnet restore
dotnet run

Then open in browser:

http://localhost:5000 (or the port shown in terminal)

How to Use (UI Flow)

Enter Client ID and Client Secret in the UI and click Log In

Go to:

AppBundles tab → browse / create bundles, versions, aliases

Activities tab → browse / create activities, versions, aliases

Select an Activity Alias → click Run to start a WorkItem

Go to WorkItems tab → check status, view report/output

Important Notes

Client Secret should never be committed to GitHub

WorkItems require input/output files as publicly accessible URLs

You can generate URLs using APS OSS (buckets/objects) or any public file hosting

API Endpoints (example)

These may differ depending on your controllers/routes:

POST /api/auth/token → generate token

GET /da/appbundles/treeNode?id=... → tree view nodes

GET /da/activities/treeNode?id=... → tree view nodes

POST /da/workitems → start workitem

GET /da/workitems/{id} → workitem status/report



