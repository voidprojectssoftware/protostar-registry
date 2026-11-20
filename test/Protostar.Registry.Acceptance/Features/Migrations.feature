Feature: The registry builds its schema on an empty database
  As an operator deploying the registry for the first time
  I want it to create its tables when it starts
  So that a new deployment needs nothing but an empty database

  The registry applies its EF Core migrations at startup, before it serves (registry#61).

  Scenario: Starting against an empty database applies every migration
    Given an empty database
    When the registry starts against it
    Then every migration the registry ships is applied
    And the model has no changes that lack a migration
    And the registry reports itself healthy
