Feature: Registering a machine is idempotent under concurrency
  As a developer whose hooks may start several backups at once on a new machine
  I want simultaneous registrations of the same machine to agree
  So that the machine exists once and no request fails

  Several requests registering the same new machine id race to insert it. One wins with 201, the rest see
  the winner's row and answer 200, and none fails with 500 (registry#41).

  Scenario: Ten parallel registrations of one new machine give one machine and no errors
    Given a running registry
    And the user "alice" is signed in
    When alice registers the same new machine 10 times in parallel
    Then every registration succeeds
    And exactly one registration answered 201 and the rest 200
    And alice has exactly one machine with that id
