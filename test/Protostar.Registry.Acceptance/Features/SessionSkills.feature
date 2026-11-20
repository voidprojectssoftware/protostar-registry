Feature: See which skills a session used
  As a developer reading back a session, or an agent reading it for me
  I want to see which skills the session invoked and which registry version each one was
  So that I can tell what shaped the session's work, and which skills it relied on

  The registry picks skill invocations out of a Claude Code transcript as it parses it (registry#14): the model's
  Skill tool calls, and the user's slash commands that load a skill. A transcript records only the name, so a
  matched skill's version is inferred: the newest version pushed no later than two minutes after the session last
  used it. The two minutes allow for the CLI's capture hook, which backs a skill up just after it is used (registry#81).
  Times in these scenarios are real: a skill is pushed, then the session uses it "now". A push "an hour after the
  session" is dated an hour on, standing for a later edit.

  Background:
    Given a running registry
    And the user "alice" is signed in

  Scenario: Each skill a session used is listed with the version in force when it was used
    Given alice has pushed the skills "deploy" and "review"
    And alice has backed up a session that used "deploy" twice and "review" once, passing arguments "SECRET-ARGS"
    And an hour after the session, alice pushed a second version of "deploy"
    When alice asks which skills the session used
    Then the session used "deploy" 2 times, matched to her skill at inferred version 1
    And the session used "review" 1 time, matched to her skill at inferred version 1
    And neither the skills the registry kept nor its answer hold "SECRET-ARGS"

  Scenario: A skill the user loaded with a slash command counts, a built-in command does not
    Given alice has pushed the skills "deploy" and "review"
    And alice has backed up a session in which she typed "/review" and "/clear"
    When alice asks which skills the session used
    Then the session used "review" 1 time, matched to her skill at inferred version 1
    And the session used no other skills

  Scenario: A plugin's skill is listed but matches no registry skill
    Given alice has pushed the skills "brainstorm" and "review"
    And alice has backed up a session that used "superpowers:brainstorm"
    When alice asks which skills the session used
    Then the session used "superpowers:brainstorm" 1 time, matched to no skill
    And the session used no other skills

  Scenario: A skill deleted since the session is marked deleted
    Given alice has pushed the skills "deploy" and "review"
    And alice has backed up a session that used "deploy"
    And alice has since deleted the skill "deploy"
    When alice asks which skills the session used
    Then the session used "deploy" 1 time, deleted from the registry

  Scenario: A skill edited then used is the version the capture hook pushed just after the use
    Given alice has pushed the skills "deploy" and "review"
    And alice has backed up a session that used "deploy"
    And the capture hook pushed her edited "deploy" as version 2 just after the use
    When alice asks which skills the session used
    Then the session used "deploy" 1 time, matched to her skill at inferred version 2

  Scenario: A skill used for the first time is version 1 once the capture hook pushes it
    Given alice has backed up a session that used "deploy"
    And the capture hook pushed "deploy" as version 1 just after the use
    When alice asks which skills the session used
    Then the session used "deploy" 1 time, matched to her skill at inferred version 1

  Scenario: A skill used before it was ever pushed has no version
    Given alice has backed up a session that used "deploy"
    And an hour after the session, alice pushed the skills "deploy" and "review"
    When alice asks which skills the session used
    Then the session used "deploy" 1 time, matched to her skill with no version

  Scenario: A skill a subagent used appears on its session, and on the subagent
    Given alice has pushed the skills "deploy" and "review"
    And alice has backed up a session that used "review"
    And a subagent of that session used "deploy"
    When alice asks which skills the session used
    Then the session used "review" 1 time, matched to her skill at inferred version 1
    And the session used "deploy" 1 time, matched to her skill at inferred version 1
    When alice asks which skills the subagent used
    Then the subagent used "deploy" 1 time, matched to her skill at inferred version 1
    And the subagent used no other skills

  Scenario: A personal skill wins over a project skill of the same name, as Claude Code loads them
    Given alice has pushed a personal skill "deploy" and a skill "deploy" in her project "demo"
    And alice has backed up a session in "demo" that used "deploy"
    When alice asks which skills the session used
    Then the session's "deploy" is the personal skill, not the project's

  Scenario: Another user sees nothing of a session's skills
    Given the user "bob" is signed in
    And alice has pushed the skills "deploy" and "review"
    And alice has backed up a session that used "deploy"
    When bob asks which skills alice's session used
    Then bob is refused with 404

  Scenario: A session parsed by an older registry gains its skills when reprocessed
    Given alice has pushed the skills "deploy" and "review"
    And alice has backed up a session that used "deploy"
    And it was parsed by a registry from before skills were recognised
    When alice asks which skills the session used
    Then the session used no skills
    When a reprocessing pass runs
    And alice asks which skills the session used
    Then the session used "deploy" 1 time, matched to her skill at inferred version 1
