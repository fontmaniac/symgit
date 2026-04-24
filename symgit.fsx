#!/usr/bin/env -S dotnet fsi

open System
open System.IO
open System.Diagnostics

// -----------------------------
// Shell helpers
// -----------------------------

let runGit (repoPath: string) (args: string) : string =
    let psi = ProcessStartInfo()
    psi.FileName <- "git"
    psi.Arguments <- args
    psi.WorkingDirectory <- repoPath
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false
    psi.CreateNoWindow <- true

    use proc = new Process()
    proc.StartInfo <- psi
    proc.Start() |> ignore

    let stdout = proc.StandardOutput.ReadToEnd()
    let stderr = proc.StandardError.ReadToEnd()
    proc.WaitForExit()

    if proc.ExitCode <> 0 then
        failwithf "git %s failed:\n%s\n%s" args stdout stderr

    stdout

// -----------------------------
// Command‑line argument parsing
// -----------------------------

type Flags =
    { Tag : string option
      Msg : string option
      Repo : string option
      Remote : string option }

let empty =
    { Tag = None
      Msg = None
      Repo = None
      Remote = None }

// -----------------------------
// Command-line argument parsing
// -----------------------------

type Command =
    | Prepare of Tag:string
    | CommitOnly of Msg:string
    | Commit of Tag:string * Msg:string
    | Checkout
    | Update
    | Init of RepoPath:string * RemoteUrl:string option
    | Move of RepoPath:string * RemoteUrl:string option

type Target =
    | Single of string
    | All

type Args =
    { Command : Command
      Target  : Target }

let rec parseOptionsRec (state: Flags) (args: string list) =
    match args with
    | [] -> state
    | "-tag" :: value :: rest ->
        parseOptionsRec { state with Tag = Some value } rest
    | "-msg" :: value :: rest ->
        parseOptionsRec { state with Msg = Some value } rest
    | "-repo" :: value :: rest ->
        parseOptionsRec { state with Repo = Some value } rest
    | "-remote" :: value :: rest ->
        parseOptionsRec { state with Remote = Some value } rest
    | unknown :: rest ->
        printfn "Unknown argument: %s" unknown
        parseOptionsRec state rest

let parseArgs (argv: string[]) =
    if argv.Length < 2 then
        failwith "Usage: symgit <command> (<hash-file>|-all) [options]"

    // 2. Parse target
    let targetArg = argv.[1]
    let target =
        if targetArg = "-all" then All
        else Single targetArg

    // 3. Parse remaining flags
    let flags = parseOptionsRec empty (argv.[2..] |> Array.toList)

    // 1. Parse command
    let command =
        match argv.[0] with
        | "prepare"    -> Prepare (flags.Tag |> Option.get)
        | "commit"     -> Commit (flags.Tag |> Option.get, flags.Msg |> Option.get)
        | "commitOnly" -> CommitOnly (flags.Msg |> Option.get)
        | "checkout"   -> Checkout
        | "update"     -> Update
        | "init"       -> Init (flags.Repo |> Option.get, flags.Remote)
        | "move"       -> Move (flags.Repo |> Option.get, flags.Remote)
        | other        -> failwithf "Unknown command: %s" other

    { Command = command
      Target  = target }

type Manifest = 
    { ManifestId : string
      ContentPath : string
      RemoteUrl : string
      Sundries : string }
with 
    override this.ToString () =
        String.concat "\n"
            [ this.ManifestId
              this.ContentPath
              this.RemoteUrl
              ""
              this.Sundries 
            ]
    static member MakeNew contentPath remoteUrl =
        { ManifestId = ""
          ContentPath = contentPath
          RemoteUrl = remoteUrl 
          Sundries = "" }

type ManifestKind = Parent | Submodule
let readManifest kind (hashFile: string) =
    if not (File.Exists hashFile) then
        failwithf "Manifest file '%s' does not exist. Cannot infer contentPath." hashFile

    let lines = File.ReadAllLines(hashFile)
    match kind with 
    | Parent ->
        if lines.Length < 2 then
            failwithf "Manifest file '%s' is malformed. Expected at least 2 lines." hashFile
    | Submodule -> 
        if lines.Length < 1 then
            failwithf "Manifest file '%s' is malformed. Expected at least manifest Id to be present at first line." hashFile

    let manifestId  = lines.[0].Trim()
    let contentPath = if lines.Length >= 2 then lines.[1].Trim() else ""
    let remoteUrl   = if lines.Length >= 3 then lines.[2].Trim() else ""
    let sundries    = if lines.Length >= 5 then String.concat "\n" lines.[4..] else ""

    { ManifestId = manifestId 
      ContentPath = contentPath
      RemoteUrl = remoteUrl 
      Sundries = sundries }

let readParentManifest = readManifest Parent
let readSubManifest = readManifest Submodule

// -----------------------------
// Functional helpers
// -----------------------------

/// Stage all modified files under repoPath.
let stageChanges (repoPath: string) =
    // git add -A .
    runGit repoPath "add -A ."
    |> ignore

/// Generate the manifest text for this commit.
let generateManifest (repoPath: string) (tag: string) (contentPath: string) (remoteUrl: string) =
    // Timestamp in ISO8601 UTC
    let timestamp =
        DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")

    // Unique GUID
    let guid = Guid.NewGuid().ToString("N")

    // Manifest ID (first line)
    let manifestId =
        sprintf "MANIFEST-%s-%s-%s" timestamp tag guid

    // Get git status in porcelain mode
    let status =
        runGit repoPath "status --porcelain"
        |> fun s -> s.Trim().Split([|'\n'|], StringSplitOptions.RemoveEmptyEntries)
        |> Array.toList

    // Convert porcelain lines into (+/-/*) entries
    let classify (line: string) =
        // Porcelain format: XY <path>
        // X = staged, Y = unstaged
        // We treat A/M/D uniformly across X or Y
        let code = line.Substring(0, 2)
        let path = line.Substring(3)

        let symbol =
            if code.Contains("A") then "+"
            elif code.Contains("D") then "-"
            elif code.Contains("M") then "*"
            else "?"

        symbol, path

    let diffLines =
        status
        |> List.map classify
        |> List.map (fun (sym, path) -> sprintf "%s %s" sym path)

    printfn "Generated manifest ID: '%s'" manifestId

    { ManifestId = manifestId 
      ContentPath = contentPath
      RemoteUrl = remoteUrl 
      Sundries = String.concat "\n" diffLines}

/// Returns true if there is anything to commit (staged or unstaged).
let hasChanges (repoPath: string) =
    let status = runGit repoPath "status --porcelain"
    not (String.IsNullOrWhiteSpace status)

/// Write manifest into repo and stage it.
let writeManifestToRepo (repoPath: string) (manifest: Manifest) =
    let manifestPath = Path.Combine(repoPath, ".manifest")
    let manifest = { manifest with ContentPath = ""; RemoteUrl = "" }
    File.WriteAllText(manifestPath, manifest.ToString ())
    runGit repoPath "add .manifest" |> ignore

/// Commit staged changes in repo, but only if there is something to commit.
let commitRepo (repoPath: string) (msg: string) =
    if hasChanges repoPath then
        let safeMsg = msg.Replace("\"", "\\\"")
        runGit repoPath (sprintf "commit -m \"%s\"" safeMsg) |> ignore
    else
        printfn "No changes detected in %s — skipping commit." repoPath

let cloneRepo (repoPath: string) (remoteUrl: string) (hashFile: string) =
    if String.IsNullOrWhiteSpace remoteUrl then
        printfn "Warning: No remote URL in manifest '%s'. Cannot clone." hashFile
    else
        printfn "Cloning '%s' into '%s'..." remoteUrl repoPath
        // Ensure leaf directory exists
        Directory.CreateDirectory(repoPath) |> ignore
        // Clone into the current directory
        runGit repoPath (sprintf "clone \"%s\" .\"" remoteUrl) |> printfn "%s"

let pullRepo (repoPath: string) =
    printfn "Pulling latest changes in '%s'..." repoPath
    runGit repoPath "pull" |> printfn "%s"

let initRepo repoPath =
    // Ensure repo folder exists
    Directory.CreateDirectory(repoPath) |> ignore
    // git init
    printfn "Initializing git repo at '%s'..." repoPath
    runGit repoPath "init" |> printfn "%s"

/// Write manifest text to external hash file.
let writeParentManifest (hashFile: string) (manifest: Manifest) =
    File.WriteAllText(hashFile, manifest.ToString())

let isValidRepo (path: string) =
    Directory.Exists(Path.Combine(path, ".git"))

let moveDirectory oldPath newPath =
    let parent = Directory.GetParent(newPath).FullName
    Directory.CreateDirectory(parent) |> ignore
    Directory.Move(oldPath, newPath)

let normalizeForGitignore (p: string) =
    p.TrimEnd('\\', '/')
     .Replace("\\", "/")
     |> fun x -> if x.StartsWith("./") then x.Substring(2) else x
     |> fun x -> if x.StartsWith("/") then x else "/" + x


let updateGitignore (contentPath: string) =
    let gitignore = ".gitignore"

    let basePath = normalizeForGitignore contentPath

    let line1 = basePath + "/*"
    let line2 = basePath + "/"

    let existing =
        if File.Exists gitignore then File.ReadAllLines gitignore |> Array.toList
        else []

    let needed =
        [ line1; line2 ]
        |> List.filter (fun l -> not (existing |> List.contains l))

    if needed <> [] then
        File.AppendAllLines(gitignore, needed)

let cleanGitignore (contentPath: string) =
    let gitignore = ".gitignore"

    if not (File.Exists gitignore) then
        // Nothing to clean
        ()
    else
        let basePath = normalizeForGitignore contentPath

        let line1 = basePath + "/*"
        let line2 = basePath + "/"

        let toRemove = Set.ofList [ line1; line2 ]

        let existing = File.ReadAllLines gitignore |> Array.toList

        let cleaned =
            existing
            |> List.filter (fun l -> not (toRemove.Contains(l.Trim())))

        // Only rewrite if something actually changed
        if cleaned <> existing then
            File.WriteAllLines(gitignore, cleaned)

/// Lookup the manifest string in repo history and return the matching revision hash.
let findRevisionByManifest (repoPath: string) (manifestId: string) : string =
    printfn "Looking for manifest ID: '%s'" manifestId
    // We search the patch history of .manifest for the manifestId.
    // --follow ensures history is tracked across renames.
    // -p prints patches so we can search inside them.
    let logOutput =
        runGit repoPath "log -p --follow --all -- .manifest"

    // Split into commits. Each commit starts with "commit <hash>"
    let commits =
        logOutput.Split([|"commit "|], StringSplitOptions.RemoveEmptyEntries)
        |> Array.toList

    // Find the commit whose patch contains the manifestId
    let matching =
        commits
        |> List.tryFind (fun block -> block.Contains(sprintf "+%s" manifestId))

    match matching with
    | None ->
        failwithf "Manifest ID '%s' not found in .manifest history." manifestId

    | Some block ->
        // The block begins with "<hash>\n..."
        let firstLine =
            block.Split([|'\n'|], StringSplitOptions.RemoveEmptyEntries)
            |> Array.head

        // firstLine is "<hash>"
        firstLine.Trim()

/// Checkout the given revision.
let checkoutRevision (repoPath: string) (revision: string) =
    runGit repoPath (sprintf "checkout %s" revision) |> ignore

// -----------------------------
// Command handlers
// -----------------------------

let handlePrepare hashFile tag =
    let bm = readParentManifest hashFile
    printfn "symgit manifest:'%s' (repo:'%s') mode:prepare tag:'%s'" hashFile bm.ContentPath tag
    if hasChanges bm.ContentPath then
        stageChanges bm.ContentPath
        let parentManifest = generateManifest bm.ContentPath tag bm.ContentPath bm.RemoteUrl
        writeManifestToRepo bm.ContentPath parentManifest
        writeParentManifest hashFile parentManifest
    else
        printfn "No changes detected — nothing to prepare."

let handleCommitOnly hashFile msg =
    let bm = readParentManifest hashFile
    printfn "symgit manifest:'%s' (repo:'%s') mode:commitOnly msg:'%s'" hashFile bm.ContentPath msg
    if hasChanges bm.ContentPath then
        commitRepo bm.ContentPath msg
    else
        printfn "No changes detected — nothing to commit."

let handleCommit hashFile tag msg =
    let bm = readParentManifest hashFile
    printfn "symgit manifest:'%s' (repo:'%s') mode:commit tag:'%s' msg:'%s'" hashFile bm.ContentPath tag msg
    if hasChanges bm.ContentPath then
        stageChanges bm.ContentPath
        let parentManifest = generateManifest bm.ContentPath tag bm.ContentPath bm.RemoteUrl
        writeManifestToRepo bm.ContentPath parentManifest
        writeParentManifest hashFile parentManifest
        commitRepo bm.ContentPath msg
    else
        printfn "No changes detected — nothing to commit."

let handleCheckout hashFile =
    let bm = readParentManifest hashFile
    printfn "symgit manifest:'%s' (repo:'%s') mode:checkout" hashFile bm.ContentPath
    let rev = findRevisionByManifest bm.ContentPath bm.ManifestId
    printfn "Revision found: '%s'" rev
    checkoutRevision bm.ContentPath rev

let handleUpdate hashFile =
    let bm = readParentManifest hashFile
    printfn "symgit manifest:'%s' (repo:'%s') mode:update" hashFile bm.ContentPath

    if isValidRepo bm.ContentPath then pullRepo bm.ContentPath
    else if (String.IsNullOrWhiteSpace(bm.RemoteUrl) |> not) then cloneRepo bm.ContentPath bm.RemoteUrl hashFile
    else printfn "No remote configured for '%s'. Unable to update." hashFile

let handleInit hashFile repoPath maybeRemote =
    printfn "symgit manifest:'%s' mode:init repo:'%s'" hashFile repoPath

    if File.Exists hashFile then
        printfn "Manifest '%s' already exists. Use 'move' instead." hashFile
    else
        let manifest = Manifest.MakeNew repoPath (defaultArg maybeRemote "")
        writeParentManifest hashFile manifest
        initRepo manifest.ContentPath
        updateGitignore manifest.ContentPath

        printfn "Init complete."

let handleMove hashFile newPath maybeRemote =
    printfn "symgit manifest:'%s' mode:move new-path:'%s'" hashFile newPath

    if not (File.Exists hashFile) then
        printfn "Manifest '%s' does not exist. Cannot move." hashFile
    else
        let oldm = readParentManifest hashFile
        let newm = { oldm with ContentPath = newPath; RemoteUrl = defaultArg maybeRemote oldm.RemoteUrl }

        if Path.GetFullPath(oldm.ContentPath) = Path.GetFullPath(newm.ContentPath) then
            writeParentManifest hashFile newm
            updateGitignore newm.ContentPath
            printfn "Paths identical. Manifest updated."

        elif not (isValidRepo oldm.ContentPath) then
            printfn "Warning: '%s' is not a valid repo. Aborting move." oldm.ContentPath

        elif Directory.Exists newm.ContentPath then
            printfn "Warning: Destination '%s' already exists. Aborting move." newm.ContentPath

        else
            // Perform move
            moveDirectory oldm.ContentPath newm.ContentPath

            writeParentManifest hashFile newm
            cleanGitignore oldm.ContentPath
            updateGitignore newm.ContentPath

            printfn "Move complete."

// -----------------------------
// Dispatcher
// -----------------------------

let processHashFile mode hashFile =
    match mode with
    | Prepare (tag) -> handlePrepare hashFile tag
    | CommitOnly (msg) -> handleCommitOnly hashFile msg
    | Commit (tag, msg) -> handleCommit hashFile tag msg
    | Checkout -> handleCheckout hashFile
    | Update -> handleUpdate hashFile
    | Init (newPath, maybeRemote) -> handleInit hashFile newPath maybeRemote
    | Move (newPath, maybeRemote) -> handleMove hashFile newPath maybeRemote

// -----------------------------
// Main entry point
// -----------------------------

let args = parseArgs fsi.CommandLineArgs.[1..]

match args.Target with
| Single file -> processHashFile args.Command file
| All ->
    let files = Directory.GetFiles(".", "*.symgit", SearchOption.TopDirectoryOnly)
    if files.Length = 0 then printfn "No .symgit files found in repo root."
    files |> Array.iter (processHashFile args.Command)
