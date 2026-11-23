Feature: Custom agents are backed up like skills
  As a developer writing custom agents on more than one machine
  I want each agent backed up with its history, and a push from an out-of-date copy refused
  So that an agent I wrote on one machine is never lost or silently overwritten

  Agents are a sibling module of skills (ADR 0003): one Markdown file each, named by the `name` in its front matter,
  personal or in a project, with the base-version and tombstone rules of skills (registry#55, #59) under their own
  problem types.

  Background:
    Given a running registry
    And the user "alice" is signed in
    And alice has pushed the agent "reviewer" twice, reaching version 2

  Scenario: A push based on an older version is refused as stale
    When alice pushes different content for the agent "reviewer" based on version 1
    Then the agent push is refused with 409 and problem type "https://protostar.dev/problems/stale-agent-version"
    And the refusal says the current version is 2
    And alice's agent "reviewer" is still at version 2

  Scenario: A push based on the current version is stored, and the same file again is unchanged
    When alice pushes different content for the agent "reviewer" based on version 2
    Then the agent push is stored as version 3
    And pushing the same file again answers 200 at version 3

  Scenario: A push from a copy of a deleted agent is refused
    Given alice has deleted the agent "reviewer"
    When alice pushes different content for the agent "reviewer" based on version 2
    Then the agent push is refused with 409 and problem type "https://protostar.dev/problems/agent-deleted"
    And the refusal says the last version was 2
    And alice has no agent named "reviewer"
    And alice's removed agents include "reviewer" at version 2

  Scenario: A push from a copy of a renamed agent is refused and names the new name
    Given alice has renamed the agent "reviewer" to "critic"
    When alice pushes different content for the agent "reviewer" based on version 2
    Then the agent push is refused with 409 and problem type "https://protostar.dev/problems/agent-renamed"
    And the refusal says the agent was renamed to "critic"
    And alice has no agent named "reviewer"

  Scenario: A fresh push of a deleted name starts a new agent numbered after the old one
    Given alice has deleted the agent "reviewer"
    When alice pushes different content for the agent "reviewer" based on version 0
    Then the agent push is stored as version 3

  Scenario: A file whose front matter has no name is refused
    When alice pushes an agent file with no name in its front matter
    Then the agent push is refused with 400 and a title mentioning "has no 'name'"

  Scenario: An agent in a subfolder is named by its front matter, and its path is kept
    When alice pushes the agent "linter" from "checks/style.md"
    Then alice's agent list has "linter" at path "checks/style.md"

  Scenario: A personal agent and a project agent of the same name keep separate histories
    When alice pushes the agent "reviewer" in the project of a folder
    Then the agent push is stored as version 1
    And alice's agent "reviewer" is still at version 2

  Scenario: Project agents follow a folder when it is linked, and back when it is unlinked
    Given alice has the agent "notes-helper" in the project of the folder "/home/alice/notes"
    And alice has a repository project that already has a skill named "notes-helper"
    When alice links the folder "/home/alice/notes" to the repository project
    Then the link answers that the agent "notes-helper" moved
    And alice's agent "notes-helper" is in the repository project
    When alice unlinks the folder "/home/alice/notes"
    Then the link answers that the agent "notes-helper" moved
    And alice's agent "notes-helper" is not in the repository project

  Scenario: A link is refused when the target already has an agent of the same name
    Given alice has the agent "notes-helper" in the project of the folder "/home/alice/notes"
    And alice has a repository project that already has an agent named "notes-helper"
    When alice links the folder "/home/alice/notes" to the repository project
    Then the link is refused with 409 naming the agent "notes-helper"
