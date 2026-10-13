# Unreleased

Changes after `0.4.148-beta`:

- Keep Auto Chat stoppable when optional lifecycle or status notifications fail. Release run ownership after a startup error, request cancellation before notifying observers, and let repeated Stop requests join the same provider-cleanup barrier.
- Share failure containment across control-plane event subscribers and arena notifications. Continue delivering to healthy observers, retire disposed subscribers, and preserve saved turn and narration results when a status view or lifecycle observer fails.
- Avoid repeating successful narration feedback when voice playback starts without a warning. A stop requested during startup now finishes with an accurate stopped status before any model call.
- Keep activity-log failures and held diagnostic leases from aborting model calls, fallback, repair, Internet-assisted replies, narration, decision cards, or output continuation. Share a 500 ms diagnostic wait and one incomplete-evidence warning per causal operation; preserve caller cancellation and authoritative snapshot failures, observe late atomic writes without replay, and keep ordinary event ordering intact.
- Use the shared fallback policy for narration, operator-to-narrator requests, and decision cards. Preserve the original terminal outcome or accepted partial response instead of silently switching models; keep eligible empty transport failures able to use their configured fallback.
