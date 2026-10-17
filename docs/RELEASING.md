# Release automation

GitHub Actions builds, tests, packages, and synchronizes this repository.

## Workflows

- `.github/workflows/ci-sync.yml` runs on every push to `master` or `dev`, including merge commits. It builds and tests on Windows, creates the portable ZIP, stores it as a workflow artifact, and pushes the successful branch commit to Gitee.
- `.github/workflows/release.yml` runs on every `v*` tag. Windows rebuilds and tests the tagged commit, creates the ZIP/checksum, and publishes GitHub; a dependent Ubuntu job downloads the exact same artifact, pushes the tag, and publishes Gitee.
- The release workflow can also be run manually for an existing tag. This is useful for repairing or backfilling release assets.
- Historical tags created before the automation scripts are supported: application sources come from the tag while release tooling falls back to the current `master` version.

## One-time secrets

Add these Actions secrets in the GitHub repository under **Settings → Secrets and variables → Actions**:

- `GITEE_SSH_PRIVATE_KEY`: the private half of a dedicated SSH key whose public half has write access to `git@gitee.com:harubilira/liraoke.git`.
- `GITEE_TOKEN`: a Gitee personal access token with repository/project permissions, used only to create releases and manage release attachments.

Do not reuse a personal daily-use SSH key. Generate a dedicated automation key without a passphrase, add only its public key to Gitee, and store only its private key in the encrypted GitHub secret.

If either secret is absent, GitHub build/release tasks still run, but the corresponding Gitee operation is skipped.

## Normal release

1. Update `<Version>` in `src/KaraokeFilmMaker.App/KaraokeFilmMaker.App.csproj`.
2. Commit and push the change to `master`.
3. Wait for **Build, test, package, and sync** to succeed.
4. Create and push an annotated tag whose numeric portion matches the project version:

   ```powershell
   git tag -a v0.5.0.0 -m "Liraoke 0.5.0.0"
   git push github v0.5.0.0
   ```

The tag workflow pushes the same tag to Gitee and publishes both platform releases. A suffix such as `-beta` marks newly created releases as prereleases while still validating against the numeric project version.

## Backfill an existing release

Open the GitHub repository's **Actions → Publish release → Run workflow**, enter the existing tag, and run it. The workflow preserves an existing platform release while replacing same-named ZIP/checksum attachments.

## Security and behavior

- Branch synchronization occurs only after a successful build, test, and package job.
- GitHub-hosted Windows runners have no default audio endpoint, so CI excludes `WasapiPlaybackTests`; run the complete suite on a Windows machine with an audio device before a public release.
- The workflows never force-push branches or tags.
- Release assets are built from the tagged commit, not from the latest branch head.
- GitHub's automatically generated “Source code” archives are source snapshots. Users should download the uploaded `Liraoke-*-win-x64.zip` asset.
