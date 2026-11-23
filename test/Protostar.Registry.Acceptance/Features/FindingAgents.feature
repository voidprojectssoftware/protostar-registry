Feature: Find and read my custom agents
  As a developer, or an agent working for me
  I want to search my custom agents by what they do and read one
  So that I can delegate the same way again (protostar-cli#126)

  An agent's whole file is indexed, redacted like everything else, and found only with kind=agent, so a client from
  before agents were searchable never gets a hit it cannot show. {github-token} stands for a well-formed GitHub token,
  assembled from parts by the steps.

  Background:
    Given a running registry
    And the user "alice" is signed in

  Scenario: A search for what an agent does finds it, with no secret in the index
    Given alice has pushed the agent "migrator" saying "Plans flamingo database migrations with token {github-token}"
    When alice searches her agents for "flamingo"
    Then the search finds her agent "migrator" at version 1
    And the agent hit's snippet shows "redacted:github-token" and not "{github-token}"
    When alice searches her agents for "{github-token}"
    Then the agent search finds nothing

  Scenario: Agents are searched only when asked for
    Given alice has pushed the agent "migrator" saying "Plans flamingo database migrations"
    When alice searches for "flamingo" with no kind
    Then the search without a kind finds nothing

  Scenario: A search finds the agent's current version, and a deleted agent no more
    Given alice has pushed the agent "migrator" saying "Plans flamingo database migrations"
    And alice has pushed the agent "migrator" saying "Plans pelican database migrations"
    When alice searches her agents for "flamingo"
    Then the agent search finds nothing
    When alice searches her agents for "pelican"
    Then the search finds her agent "migrator" at version 2
    Given alice has deleted the agent "migrator"
    When alice searches her agents for "pelican"
    Then the agent search finds nothing

  Scenario: Another user's agents are not searched
    Given the user "bob" is signed in
    And alice has pushed the agent "migrator" saying "Plans flamingo database migrations"
    When bob searches his agents for "flamingo"
    Then the agent search finds nothing

  Scenario: An agent is read whole, or redacted for an agent to read
    Given alice has pushed the agent "migrator" saying "Plans flamingo database migrations with token {github-token}"
    When alice reads her agent "migrator"
    Then the file is the one she pushed, hashing to its content hash
    When alice reads her agent "migrator" redacted
    Then the file shows "[redacted:github-token]" and not "{github-token}"

  Scenario: A version that does not exist, or another user's agent, is not found
    Given the user "bob" is signed in
    And alice has pushed the agent "migrator" saying "Plans flamingo database migrations"
    Then reading version 2 of alice's agent "migrator" answers 404
    And bob reading the agent "migrator" answers 404
