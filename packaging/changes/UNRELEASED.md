# Unreleased

Changes after `0.4.150-beta`:

- Reduce temporary allocations while validating saved experiment artifacts by reusing decoded property names and avoiding absolute-path matching for values without path separators. Privacy, duplicate-member, and canonical-format checks remain unchanged.
