# Contributing

Thank you for helping FLIP. Bug reports, fixes and translations are welcome.

## Reporting a problem

Open an [issue](https://github.com/thetinyfoundry/FLIP/issues) and include the edition (Microsoft Store or GitHub), FLIP and Windows versions, the source and target formats, and the message shown. Do not attach private files: a small sample that reproduces the problem is enough.

## Changing the code

1. Build and test as described in the [README](README.md#build-from-source). The build must have no warnings.
2. Keep each change focused and add a check that fails without it.
3. Never modify the user's source files and never overwrite an output unless the user has chosen it.
4. Test file signatures, name collisions, cancellation and cleanup when you change an engine.
5. Test swapped mouse buttons and both key and drag orders when you change input handling. CI does not prove a physical drag from File Explorer.

Do not commit converted files, logs, local paths, credentials, or third-party programs. `scripts/Audit-Repository.ps1` checks for common mistakes and runs in CI.

Contributions are accepted under the project license, [GNU GPLv3](LICENSE).
