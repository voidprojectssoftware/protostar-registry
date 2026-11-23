Feature: See which of my custom agents a session used
  As a developer reading back a session, or an agent reading it for me
  I want to see which of my custom agents the session delegated to, and which version each one was
  So that the same delegation can be repeated instead of improvised (protostar-cli#126)

  The registry records every agent type a Claude Code session delegated to (Agent or Task tool calls, by
  subagent_type) and, when asked, lists only the ones that are the owner's agents in the registry. Built-in types
  (general-purpose, Explore, Plan) and plugin agents (plugin:agent) never appear. A transcript records only the name,
  so the version is inferred: the newest version pushed no later than two minutes after the session ended, since the
  CLI backs agents up when a session ends. Times are real: a push "an hour after the session" is dated an hour on.

  Background:
    Given a running registry
    And the user "alice" is signed in

  Scenario: Only the custom agent is listed, with the version in force, from the session and its subagent
    Given alice has pushed the agent "reviewer"
    And alice has backed up a session that delegated to "reviewer", "general-purpose" and "superpowers:code-reviewer", with the prompt "SECRET-PROMPT"
    And a subagent of that session delegated to "reviewer", "Explore" and "superpowers:code-reviewer"
    And an hour after the session, alice pushed a second version of the agent "reviewer"
    When alice asks which agents the session used
    Then the session used only "reviewer", 2 times, as her personal agent at inferred version 1
    And neither the agents the registry kept nor its answer hold "SECRET-PROMPT"
    When alice asks which agents the subagent used
    Then the subagent used only "reviewer", 1 time, as her personal agent at inferred version 1

  Scenario: An older Task call counts like an Agent call
    Given alice has pushed the agent "reviewer"
    And alice has backed up a session that delegated to "reviewer" through the Task tool
    When alice asks which agents the session used
    Then the session used only "reviewer", 1 time, as her personal agent at inferred version 1

  Scenario: An agent created during the session is the version pushed when the session ended
    Given alice has backed up a session that delegated to "reviewer", "general-purpose" and "Plan", with the prompt "go"
    And the capture hook pushed the agent "reviewer" when the session ended
    When alice asks which agents the session used
    Then the session used only "reviewer", 1 time, as her personal agent at inferred version 1

  Scenario: A project agent wins over a personal agent of the same name, as Claude Code loads them
    Given alice has pushed a personal agent "reviewer" and an agent "reviewer" in her project "demo"
    And alice has backed up a session in "demo" that delegated to "reviewer"
    When alice asks which agents the session used
    Then the session's "reviewer" is the project agent, not the personal one

  Scenario: A session using none of her agents lists none
    Given alice has backed up a session that delegated to "general-purpose", "Explore" and "Plan", with the prompt "go"
    When alice asks which agents the session used
    Then the session used no agents

  Scenario: An agent deleted since the session is marked deleted
    Given alice has pushed the agent "reviewer"
    And alice has backed up a session that delegated to "reviewer", "general-purpose" and "Plan", with the prompt "go"
    And alice has deleted the agent "reviewer"
    When alice asks which agents the session used
    Then the session used only "reviewer", 1 time, deleted from the registry

  Scenario: Another user sees nothing of a session's agents
    Given the user "bob" is signed in
    And alice has pushed the agent "reviewer"
    And alice has backed up a session that delegated to "reviewer", "general-purpose" and "Plan", with the prompt "go"
    When bob asks which agents alice's session used
    Then bob's request for the agents is refused with 404

  Scenario: A session parsed by an older registry gains its agents when reprocessed
    Given alice has pushed the agent "reviewer"
    And alice has backed up a session that delegated to "reviewer", "general-purpose" and "Plan", with the prompt "go"
    And it was parsed by a registry from before agents were recognised
    When alice asks which agents the session used
    Then the session used no agents
    When a reprocessing pass runs
    And alice asks which agents the session used
    Then the session used only "reviewer", 1 time, as her personal agent at inferred version 1
