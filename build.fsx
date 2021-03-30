#r "paket:
nuget FSharp.Core ~> 4.7
nuget Fake.DotNet.Cli
nuget Fake.IO.FileSystem
nuget Fake.Core.ReleaseNotes
nuget Fake.Core.Target
nuget Fake.Tools.Git
nuget Microsoft.Extensions.Configuration.Json
nuget Microsoft.Extensions.Configuration.Binder
nuget Farmer ~> 1.4
nuget FSharp.Formatting
nuget Fake.DotNet.FSFormatting //"
#load "./.fake/build.fsx/intellisense.fsx"
#if !FAKE
  #r "Facades/netstandard"
#endif

open Fake.Core
open Fake.Core.TargetOperators
open Fake.DotNet
open Fake.IO
open Fake.IO.Globbing.Operators
open Fake.Tools
open System
open Farmer
open Farmer.Arm
open Farmer.Builders

let gitName = "Azure.ServiceBus.DatatypeChannels"
let gitOwner = "Azure"
let gitHome = sprintf "https://github.com/%s" gitOwner
let gitRepo = sprintf "git@github.com:%s/%s.git" gitOwner gitName
let gitContent = sprintf "https://raw.githubusercontent.com/%s/%s" gitOwner gitName

let release = ReleaseNotes.load "RELEASE_NOTES.md"
let ver =
    match Environment.environVarOrNone "BUILD_NUMBER" with
    | Some n -> { release.SemVer with Patch = uint32 n; Original = None }
    | _ -> SemVer.parse "0.0.0"


[<AutoOpen>]
module Shell =
    let sh cmd args cwd = 
        Shell.Exec (cmd,args,cwd)
        |> function 0 -> () | code -> failwithf "%s %s (in %s) exited with code: %d" cmd args cwd code

    let psh cmd args cwd parse = 
        CreateProcess.fromRawCommandLine cmd args
        |> CreateProcess.withWorkingDirectory cwd
        |> CreateProcess.redirectOutput
        |> CreateProcess.ensureExitCode
        |> CreateProcess.map parse
        |> Proc.run

    module Parse =
        let plain r = String.trimChars [|' '; '\n'|] r.Result.Output

module Az =
    let query args =
        psh "az" args
    let currentUserId () =
        query "ad signed-in-user show --query objectId -o tsv" "." (Parse.plain >> Guid)
    let currentSpId principalId =
        query (sprintf "ad sp show --id %O --query objectId -o tsv" principalId) "." (Parse.plain >> Guid)

module Settings =
    open Microsoft.Extensions.Configuration
    let [<Literal>] TestSettingsJson = "tests/Azure.ServiceBus.DatatypeChannels.Tests/local.settings.json"
    type [<CLIMutable>] TestSettings = { ServiceBus: string }

    let testSettings = 
        lazy
            let config = ConfigurationBuilder().AddJsonFile((Path.getFullName ".", TestSettingsJson) ||> Path.combine, false).Build()
            let testSettings = { ServiceBus = "" }
            config.Bind testSettings
            testSettings

module ARM = // workaround for https://github.com/dotnet/fsharp/issues/6434
    let AzureServiceBusDataOwner = Guid "090c5cfd-751d-490a-894a-3ce6f1109419"

    let roleAssingment scope principalType assignmentId principalId roleDefinitionId dependsOn =
        {| apiVersion = "2020-04-01-preview"
           ``type`` = "Microsoft.Authorization/roleAssignments"
           name = string assignmentId
           scope = scope
           properties = 
               {| roleDefinitionId = sprintf "[resourceId('Microsoft.Authorization/roleDefinitions','%O')]" roleDefinitionId
                  principalType = principalType
                  principalId = principalId |}
           dependsOn = [| dependsOn |] |}

    let init (principalId, principalType) loc rg ns =
        let sb = serviceBus {
            name ns
            sku ServiceBus.Sku.Standard
        }
        let sbDataOwner = 
            roleAssingment (sprintf "[concat('Microsoft.ServiceBus/namespaces', '/', '%s')]" sb.Name.Value)
                           principalType
                           (sprintf "[guid(resourceGroup().id, '%O', '%O')]" principalId AzureServiceBusDataOwner)
                           (string principalId)
                           AzureServiceBusDataOwner
                           ((ResourceId.create (ServiceBus.namespaces, sb.Name)).Eval())
            |> Resource.ofObj
        let deployment = arm {
            location loc
            add_resource sb
            add_resource sbDataOwner
        }
        let _ = deployment |> Deploy.execute rg []

        printfn "Deployed, generating local settings..."
        [ sprintf """{ "ServiceBus": "%s.servicebus.windows.net" }""" sb.Name.Value ]
        |> File.write false Settings.TestSettingsJson

Target.create "clean" (fun _ ->
    !! "**/bin"
    ++ "**/obj"
    ++ "out"
    |> Seq.iter Shell.cleanDir
)

Target.create "restore" (fun _ ->
    DotNet.restore id "."
)

Target.create "build" (fun _ ->
    let args = "--no-restore"
    DotNet.publish (fun a -> a.WithCommon (fun c -> { c with CustomParams = Some args})) "."
)

Target.create "test" (fun _ ->
    let args = "--no-restore"
    DotNet.test (fun a -> a.WithCommon (fun c -> { c with CustomParams = Some args})) "."
)

Target.create "package" (fun _ ->
    let args = sprintf "/p:Version=%s --no-restore" ver.AsString
    DotNet.pack (fun a -> a.WithCommon (fun c -> { c with CustomParams = Some args })) "."
)

Target.create "publish" (fun _ ->
    let exec dir =
        DotNet.exec (fun a -> a.WithCommon (fun c -> { c with WorkingDirectory=dir }))
    let args = sprintf "push %s.%s.nupkg -s %s -k %s"
                       "Azure.ServiceBus.DatatypeChannels" ver.AsString
                       (Environment.environVar "NUGET_REPO_URL")
                       (Environment.environVar "NUGET_REPO_KEY")
    let result = exec ("src/Azure.ServiceBus.DatatypeChannels/bin/Release") "nuget" args
    if (not result.OK) then failwithf "%A" result.Errors
)

Target.create "meta" (fun _ ->
    [ "<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">"
      "<PropertyGroup>"
      "<Copyright>(c) Microsoft Corporation. All rights reserved.</Copyright>"
      sprintf "<PackageProjectUrl>%s/%s</PackageProjectUrl>" gitHome gitName
      "<PackageLicense>MIT</PackageLicense>"
      sprintf "<PackageReleaseNotes>%s</PackageReleaseNotes>" (List.head release.Notes)
      sprintf "<PackageIconUrl>%s/master/docs/content/logo.png</PackageIconUrl>" gitContent
      "<PackageTags>Azure;Service Bus;CEP;fsharp</PackageTags>"
      sprintf "<Version>%s</Version>" (string ver)
      sprintf "<FsDocsLogoLink>%s/master/docs/content/logo.png</FsDocsLogoLink>" gitContent
      sprintf "<FsDocsLicenseLink>%s/blob/master/LICENSE.md</FsDocsLicenseLink>" gitRepo
      sprintf "<FsDocsReleaseNotesLink>%s/blob/master/RELEASE_NOTES.md</FsDocsReleaseNotesLink>" gitRepo
      "<FsDocsNavbarPosition>fixed-right</FsDocsNavbarPosition>"
      "<FsDocsWarnOnMissingDocs>true</FsDocsWarnOnMissingDocs>"
      "<FsDocsTheme>default</FsDocsTheme>"
      "</PropertyGroup>"
      "</Project>"]
    |> File.write false "Directory.Build.props"
)

Target.create "generateDocs" (fun _ ->
   Shell.cleanDir ".fsdocs"
   DotNet.exec id "fsdocs" "build --clean" |> ignore
)

Target.create "watchDocs" (fun _ ->
   Shell.cleanDir ".fsdocs"
   DotNet.exec id "fsdocs" "watch" |> ignore
)

Target.create "releaseDocs" (fun _ ->
    let tempDocsDir = "temp/gh-pages"
    Shell.cleanDir tempDocsDir
    Git.Repository.cloneSingleBranch "" gitRepo "gh-pages" tempDocsDir

    Shell.copyRecursive "output" tempDocsDir true |> Trace.tracefn "%A"
    Git.Staging.stageAll tempDocsDir
    Git.Commit.exec tempDocsDir (sprintf "Update generated documentation for version %s" release.NugetVersion)
    Git.Branches.push tempDocsDir
)

Target.create "init" (fun p ->
    let loc, rg, privateName =
        match p.Context.Arguments with
        | [location; rg; name] -> Location location, rg, name
        | _ -> failwith "Init requires parameters: Azure region, resource group to deploy into and Service Bus namespace to create."
    let currentUser = Az.currentUserId()
    ARM.init (currentUser, "User") loc rg privateName
)

Target.create "initCI" (fun _ ->
    let loc = Location.CentralUS
    let rg = "devops-azure-datatype-channels-dev"
    let ns = "datatype-channels-ci"
    let currentUser = Environment.environVar "servicePrincipalId" |> Az.currentSpId
    ARM.init (currentUser, "ServicePrincipal") loc rg ns
)

Target.create "release" ignore
Target.create "ci" ignore

"clean"
  ==> "restore"
  ==> "meta"
  ==> "build"
  ==> "test"
  ==> "generateDocs"
  ==> "package"
  ==> "publish"

"releaseDocs"
  <== ["test"; "generateDocs" ]

"release"
  <== [ "initCI"; "publish" ]

"ci"
  <== [ "initCI"; "test" ]

Target.runOrDefaultWithArguments "test"