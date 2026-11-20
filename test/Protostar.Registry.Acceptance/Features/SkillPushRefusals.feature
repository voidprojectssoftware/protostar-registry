Feature: Pushes from an out-of-date copy of a skill are refused
  As a developer editing a skill on more than one machine
  I want a push from a stale or removed copy to be refused instead of stored
  So that a newer version or a deliberate delete is never silently overwritten

  A push names the version its copy was last synced from (baseVersion). The registry refuses one that is not
  current with 409 and a problem type the client branches on (registry#55, #59).

  Background:
    Given a running registry
    And the user "alice" is signed in
    And alice has pushed the skill "alpha" twice, reaching version 2

  Scenario: A push based on an older version is refused as stale
    When alice pushes different content for "alpha" based on version 1
    Then the push is refused with 409 and problem type "https://protostar.dev/problems/stale-skill-version"
    And the problem says the current version is 2
    And alice's skill "alpha" is still at version 2

  Scenario: A push based on the current version is stored
    When alice pushes different content for "alpha" based on version 2
    Then the push is stored as version 3

  Scenario: A push from a copy of a deleted skill is refused
    Given alice has deleted the skill "alpha"
    When alice pushes different content for "alpha" based on version 2
    Then the push is refused with 409 and problem type "https://protostar.dev/problems/skill-deleted"
    And the problem says the last version was 2
    And alice has no skill named "alpha"

  Scenario: A push from a copy of a renamed skill is refused and names the new name
    Given alice has renamed the skill "alpha" to "beta"
    When alice pushes different content for "alpha" based on version 2
    Then the push is refused with 409 and problem type "https://protostar.dev/problems/skill-renamed"
    And the problem says the skill was renamed to "beta"
    And alice has no skill named "alpha"

  Scenario: A fresh push of a deleted name starts a new skill numbered after the old one
    Given alice has deleted the skill "alpha"
    When alice pushes different content for "alpha" based on version 0
    Then the push is stored as version 3
