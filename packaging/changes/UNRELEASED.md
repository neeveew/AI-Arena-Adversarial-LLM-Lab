# Unreleased

This is the development ledger between versioned builds. Keep
`0.4.133-beta` as the declared product version until the repository owner
chooses the next version. At release time, move verified bullets into the new
versioned `.txt` change file.

## Verified

- Make Solution Doctor repair approvals verify reviewed file bytes through a
  portable streamed SHA-256 guard, including Windows PowerShell hosts where
  `Get-FileHash` is unavailable.
