# SPAdminStudio — SharePoint On-Premises Administrator Suite

**SPAdminStudio** is a modern Windows WPF desktop application built on **.NET 8** designed specifically for administrators, branding engineers, and SharePoint developers working with **SharePoint On-Premises (2013, 2016, 2019)**.

---

## 🚀 Key Capabilities

### 1. Robust Authentication & Connection Manager
* **Multi-Authentication Support**:
  * **Current Windows User** (Integrated Windows Authentication / NTLM / Kerberos)
  * **Specific Windows Domain Credentials** (Username, Password, Domain)
  * **Forms-Based Authentication (FBA)** via SOAP `Authentication.asmx` and automated cookie acquisition
* **Encrypted Profile Manager**: Save and switch between multiple site collections and farm environments. Passwords are encrypted on-disk using **Windows Data Protection API (DPAPI)**.
* **Auto-Discovery**: Resolves site title, server relative URL, current user details, and administrator status on connect.

### 2. Site Explorer (SharePoint Designer "Site Objects")
* **Site Summary**: Title, description (editable), URL, template, language, UI version, created / modified dates, permission inheritance, parent site, and the assigned site / system master pages, alternate CSS and logo.
* **Lists and Libraries**: Grouped into Document Libraries and Lists with type, item count and location. Hidden lists can be shown. Opening a library browses it in All Files; opening a list shows its items.
* **Subsites**: All child sites of the current site. Double-click (or **Open Subsite**) to move the session into it with the same credentials; **Parent Site** goes back up.
* **Open any site**: Type a site URL on the same farm in the address bar and press Enter.
* **All Files**: Browse the whole site's folder structure; open files in the editor, download, upload, create folders and delete (to the Recycle Bin).
* **Content Types / Site Columns**: Everything available to the site (including inherited), grouped by group, with hidden items optional.
* **Site Groups & Permissions**: Groups with their members, and the site's permission assignments and levels.
* **Open in Browser** for the site, a list or a subsite.

### 3. Deploy to Production (UAT → Production)
* **Two connections**: the current connection is the source (UAT); pick a saved profile as the target (Production).
* **Queue anything**: master pages, page layouts, pages, CSS/JS/images, or whole folders — from the Deploy tab's browser or with **🚚 Add to Deployment** in Master Page Gallery, Style Library and Site Explorer.
* **Analyze (dry run)**: expands folders and shows, per file, whether it is **New**, **Overwrite**, **Skip** or **Blocked** (checked out by someone else on Production) — nothing is changed.
* **Deploy**, in dependency order (master pages → page layouts → assets → pages):
  * Backs up every Production file before overwriting it, and writes a CSV report per run.
  * Creates missing folders — only inside existing libraries (never creates a plain folder in a site).
  * Checks out when the library requires it, uploads, then copies the item properties: content type (matched by name), page layout, page content, associated content type (re-mapped to Production's ID), master page UI version, etc. UAT links in those properties are rewritten to the Production address.
  * Checks in / publishes a major version and approves (configurable).
* Not copied: web parts in page zones, lookup / person / managed metadata values (farm-specific IDs).

### 4. Master Page Gallery (`_catalogs/masterpage`)
* **Set as Master Page**: Assign the selected `.master` as the site master page, system master page, or both, for the current site.
* **Live Catalog Explorer**: Lists all master pages (`.master`), page layouts (`.aspx`), HTML master templates, CSS, and JS files.
* **Metadata & Moderation Visibility**: Displays version number (`1.0`, `1.1`), checkout state, user who locked the file, and Content Approval status (`Approved`, `Draft`, `Pending`, `Denied`).
* **Bulk & Single Selection**: Toggle individual items or select all.
* **Bulk Export**: Download selected files to any chosen local folder.
* **Bulk Import**: Upload one or many files into `_catalogs/masterpage` with overwrite protection.

### 5. Style Library Explorer (`Style Library`)
* **Two-Pane Explorer**:
  * **Left**: Interactive recursive folder tree (navigates subfolders like `Themable`, `CSS`, `Images`, `JS`).
  * **Right**: Real-time file grid in the selected folder.
* **Folder Tree Export**: Recursively downloads an entire folder tree to your local computer, preserving the exact nested folder hierarchy.
* **Folder Tree Import**: Recursively uploads an entire local directory tree to SharePoint, creating missing subfolders automatically and uploading files.
* **Folder Management**: Create new folders directly in the Style Library.

### 6. Lifecycle, Check-in / Check-out & Publishing Engine
* **Check-Out**: Single or bulk checkout of files.
* **Check-In**: Single or bulk check-in with a dedicated dialog:
  * Select version type: **Minor Version (Draft)**, **Major Version (Publish)**, or **Overwrite**.
  * Add change comments.
* **Publish Major Version**: Single or bulk promotion of files to major versions.
* **Discard Check-Out (Undo)**: Reverts locked files back to the previous version.
* **Approve / Deny**: 1-click moderation approval for libraries with Content Approval enabled.

### 7. Advanced Administrative & Developer Extras
* **Built-in Code Viewer & Editor (AvalonEdit)**: Double-click any master page, CSS, or JS file to view and edit directly within the application with syntax highlighting. Includes one-click **Save to SharePoint** with auto-checkout and auto-publish options!
* **1-Click Branding Backup (Snapshot)**: Automatically downloads all Master Pages + Style Library and packages them into a timestamped `.zip` archive.
* **Live Audit Log**: Real-time event log with timestamps and one-click clipboard copy.

---

## 🛠️ Project Structure

```
SPAdminStudio/
├── Converters/
│   └── UiConverters.cs             # Status color and visibility converters
├── Models/
│   ├── ConnectionProfile.cs        # Profile & site info models
│   ├── SiteObjects.cs              # Lists, subsites, content types, columns, groups, permissions
│   └── SharePointItem.cs           # Item, folder, and transfer progress models
├── Services/
│   ├── ISharePointService.cs       # SharePoint client abstraction
│   ├── ProfileStorageService.cs    # DPAPI-encrypted profile storage
│   ├── SharePointRestService.cs    # REST API client for SP 2013/2016/2019
│   └── SharePointRestService.SiteObjects.cs # Site Explorer REST calls
├── ViewModels/
│   ├── BackupViewModel.cs          # 1-Click ZIP backup logic
│   ├── CodeViewerViewModel.cs      # In-app editor and save logic
│   ├── ConnectionViewModel.cs      # Profiles & connection logic
│   ├── MainViewModel.cs            # Root ViewModel & logging coordinator
│   ├── MasterPageGalleryViewModel.cs # Master page bulk & lifecycle logic
│   ├── SiteExplorerViewModel.cs    # Site Objects explorer & site navigation
│   └── StyleLibraryViewModel.cs    # Style library tree & bulk recursive logic
├── Views/
│   ├── CheckInDialog.xaml          # Version & comment modal dialog
│   └── NewFolderDialog.xaml        # New folder modal dialog
├── MainWindow.xaml                 # Main application UI
└── SPAdminStudio.csproj
```

---

## 🏃 Running the Application

In a terminal, navigate to the project directory and run:

```powershell
cd SPAdminStudio
dotnet run
```

### Publishing a single .exe

The project is set up to publish one self-contained, compressed `SPAdminStudio.exe` (about 70 MB) that runs on any Windows x64 machine without installing .NET — copy that one file to the admin's machine.

From a terminal in `SPAdminStudio`:

```powershell
dotnet publish SPAdminStudio.csproj -p:PublishProfile=FolderProfile
```

or in Visual Studio: right-click the project → **Publish** → **FolderProfile** → **Publish**.

The result is written to `SPAdminStudio\dist\SPAdminStudio.exe` (ignored by git).

> **If publishing fails** with *"The process cannot access the file ... because it is being used by another process"*, close every running SPAdminStudio window (including copies started from an earlier publish folder) and run `dotnet build-server shutdown`, then publish again.
> With *"MSB1011: Specify which project or solution file to use"*, delete any leftover `SPAdminStudio_*_wpftmp.csproj` file in the project folder (a temporary file from an interrupted build).
