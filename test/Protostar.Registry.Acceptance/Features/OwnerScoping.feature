Feature: Each user sees only their own data
  As a developer backing up my work to a shared registry
  I want nothing I back up to be visible to another user
  So that my skills, sessions, memory and projects stay mine

  The registry is multi-tenant: every read is filtered by the caller's identity (registry#22, #25, #34, #63, #67).
  Each scenario checks one resource from the other user's side, and first checks that alice can see it,
  so an empty answer for bob means scoping and not missing data.

  Background:
    Given a running registry
    And the users "alice" and "bob" are signed in
    And alice has backed up a skill named "alpha"
    And alice has backed up a session mentioning "aurora borealis"
    And alice has backed up project memory mentioning "aurora borealis"

  Scenario: bob cannot list or download alice's skills
    Then alice's skill list contains "alpha"
    But bob's skill list is empty
    And bob downloading the skill "alpha" is refused with 404

  Scenario: bob cannot list or read alice's sessions
    Then alice's session list contains her session
    But bob's session list is empty
    And bob reading alice's session is refused with 404
    And bob downloading alice's session is refused with 404
    And bob looking up alice's session finds it unknown

  Scenario: bob cannot list or read alice's memory
    Then alice's memory list contains her project
    But bob's memory list is empty
    And bob listing the files of alice's project is refused with 404
    And bob downloading the memory of alice's project is refused with 404

  Scenario: bob cannot reach alice's project
    When bob resolves the folder alice's memory came from
    Then bob is told the folder has no project yet
    And bob linking a folder to alice's project is refused with 404

  Scenario: bob's search does not find alice's sessions or memory
    Then alice's search for "aurora borealis" finds her session and her memory
    But bob's search for "aurora borealis" finds nothing
