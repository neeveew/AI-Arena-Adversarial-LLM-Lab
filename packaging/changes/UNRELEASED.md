# Unreleased

Changes after `0.4.148-beta`:

- Keep Auto Chat stoppable when optional lifecycle or status notifications fail. Release run ownership after a startup error, request cancellation before notifying observers, and let repeated Stop requests join the same provider-cleanup barrier.
- Share failure containment across control-plane event subscribers and arena notifications. Continue delivering to healthy observers, retire disposed subscribers, and preserve saved turn and narration results when a status view or lifecycle observer fails.
- Avoid repeating successful narration feedback when voice playback starts without a warning. A stop requested during startup now finishes with an accurate stopped status before any model call.
