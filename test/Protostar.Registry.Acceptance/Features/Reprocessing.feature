Feature: Fixes to how sessions are read reach existing sessions without upgrading
  As a developer
  I want my existing sessions reprocessed when the registry gets better at reading them
  So that what I see when listing, reading, or searching improves without me upgrading the CLI

  The registry derives each session's summary from its stored chunks and records which parser version built it
  (registry#12). A reprocessing pass rebuilds every summary below the current version from the chunks in the object
  store. "Processed by an older registry" is set up by marking a stored summary with an older parser version and a
  wrong first prompt, as a parser bug would have left it. "Holds" is a test-only row lock in Postgres (on the
  session, or on its search entries), so a step can act while the pass is waiting on that row.

  Background:
    Given a running registry
    And the user "alice" is signed in

  Scenario: An older registry's summaries are rebuilt, and listing shows every session throughout
    Given alice has backed up sessions in which she typed "fix the flaky login test" and "write the release notes"
    And both were processed by an older registry, which got their first prompt wrong
    And the test holds the session "write the release notes"
    When a reprocessing pass starts and is waiting to save that session
    Then listing alice's sessions shows both of them
    And the held session is listed with its previous first prompt
    When the test lets go of the session and the pass finishes
    Then listing alice's sessions shows the first prompts "fix the flaky login test" and "write the release notes"
    And both sessions are at the current parser version

  Scenario: An append that lands while the pass is saving the session is kept
    Given alice has backed up a session in which she typed "first question"
    And it was processed by an older registry, which got its first prompt wrong
    And the test holds the session
    When a reprocessing pass is waiting to save the session
    And alice appending "second question" is waiting to save the session too
    And the test lets go of the session
    Then the append of "second question" succeeds and the pass finishes
    And the session shows the first prompt "first question", 2 prompts and both chunks
    And the session is at the current parser version
    And the registry logged no errors

  Scenario: An append that lands while the pass is reading the session is folded into the rebuilt summary
    Given alice has backed up a session in which she typed "first question"
    And it was processed by an older registry, which got its first prompt wrong
    And its search entries were made by an older index version
    And the test holds the session's search entries
    When a reprocessing pass is waiting to reindex the session
    And alice appends "second question" to the session
    And the test lets go of the search entries
    Then the pass finishes
    And the session shows the first prompt "first question", 2 prompts and both chunks
    And the session is at the current parser version
    And the session's search entries are at the current index version
    And the registry logged no errors
