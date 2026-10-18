# Preparing a beta release

Public beta policy: unsigned Windows x64 ZIP, explicit warning, ZIP SHA-256,
per-file MANIFEST and source commit in BUILD-INFO. No command below is permission
to publish or rewrite an existing remote history.

## Verification

Build locale is pinned to `DOTNET_CLI_UI_LANGUAGE=ru-RU`, including independent
source-package builds. MSBuild's generated AssemblyInfo includes a localized
comment in its PDB checksum; an unpinned locale changes binary hashes even when
the executable logic is identical. `tools/ci.ps1` sets this environment variable.

1. Freeze a reviewed commit with current component/helper binaries and license
   inventory. The working tree must be clean. Use the SDK in `global.json` and
   the dependency lock files. Never copy global OVM libraries into a release.
2. Run the ordinary CI and an interactive Windows verification on that version:

   ```powershell
   ./tools/ci.ps1 -VerifyReleaseDll -RunUiTests -RunWordSmoke -RunPaintSmoke
   ```

3. Run the maintainer's separate Sonar checks. Do not expose a private Sonar
   service or put its credentials in a public workflow. Headless coverage is
   not a substitute for interactive coverage; record which set was analyzed.
4. Build and verify the beta, then repeat the package build from the same commit
   and compare ZIP hashes:

   ```powershell
   py -3.14 tools/build-release.py --version v0.5.0-beta.4
   py -3.14 tools/verify-dist.py dist/MakeItZBS --run-probe
   ```

5. Export a reviewed public snapshot to a new directory and scan it, including
   nested vendor archives:

   ```powershell
   py -3.14 tools/export-public-source.py dist/public-source
   py -3.14 tools/run-secret-audit.py dist/public-source
   ```

The exporter includes only the policy in `config/public-source.json`, with file
hashes and the source commit in PUBLIC-SOURCE-MANIFEST. It does not include `.git`,
private audits, installed configuration, old screenshots or logs. It does not
rewrite the original Gitea repository. An inspection export marked `dirty: true`
is not a release source snapshot.

Build and test the exported sources in a fresh checkout before upload. Establish
the initial public Git history only after the owner's review; do not push the
original internal history as a shortcut. Relative Markdown links must resolve
inside the public tree. Pinned dependencies must remain available from the
declared public sources or checked-in vendor packages.

## Publication

The public GitHub workflow verifies PRs/pushes and uploads CI artifacts; it does
not publish a release. A candidate CI artifact is not an approved beta release.
After owner approval, publish the matching source and release assets as a
prerelease. Include tested Windows/display versions, manual scenarios not yet
verified, unsigned status and known limitations. Recheck uploaded checksums.

Stable unsigned versions are outside the beta policy. A signed stable release
requires a separately agreed Authenticode certificate and protected release
process. Signing keys must not be available to pull-request builds.

See [INSTALL](../INSTALL.md), [SECURITY](../SECURITY.md),
[PRIVACY](../PRIVACY.md), [TESTING](../TESTING.md).
