Feature: The registry says which features it serves
  As a developer whose CLI may be newer than the registry it talks to
  I want the registry to name the features it has
  So that my CLI can tell me to upgrade the registry instead of failing on a 404

  GET /v1/meta lists capabilities: stable kebab-case names for the features added within API v1 (registry#15). A name
  is added in the change that ships its feature and never removed within v1. The first scenario pins the list, so a
  name cannot appear or vanish unnoticed; the outline uses each feature, so a name cannot stay listed after its
  feature is gone.

  Scenario: The meta lists every capability this registry serves
    Given a running registry
    When a client asks the registry for its meta
    Then the meta lists API major 1
    And the meta lists exactly these capabilities, in this order
      | capability               |
      | skills                   |
      | stale-skill-push         |
      | project-skills           |
      | skill-rename             |
      | skill-delete             |
      | skill-tombstones         |
      | sessions                 |
      | transcript-lookup        |
      | transcript-prefix-lookup |
      | session-skills           |
      | redacted-read            |
      | projects                 |
      | memory                   |
      | memory-content           |
      | memory-base-versions     |
      | search                   |
      | memory-redacted-read     |
      | agents                   |
      | agent-content            |
      | session-agents           |
      | agent-search             |
    And every capability the meta lists has a check of its feature

  Scenario Outline: The feature behind each listed capability works
    Given a running registry
    And the user "alice" is signed in
    And the meta lists the capability "<capability>"
    Then alice can use the feature behind "<capability>"

    Examples:
      | capability               |
      | skills                   |
      | stale-skill-push         |
      | project-skills           |
      | skill-rename             |
      | skill-delete             |
      | skill-tombstones         |
      | sessions                 |
      | transcript-lookup        |
      | transcript-prefix-lookup |
      | session-skills           |
      | redacted-read            |
      | projects                 |
      | memory                   |
      | memory-content           |
      | memory-base-versions     |
      | search                   |
      | memory-redacted-read     |
      | agents                   |
      | agent-content            |
      | session-agents           |
      | agent-search             |
