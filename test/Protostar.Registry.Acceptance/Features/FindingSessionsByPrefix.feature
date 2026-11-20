Feature: Finding a session from the start of its id
  As a developer who remembers only the first few characters of a session's id
  I want the registry to find my sessions whose id starts with them
  So that `protostar sessions show 7d3c` opens the session in one request, or tells me exactly which ones I meant

  GET /v1/transcripts takes keyPrefix (registry#74): the caller's transcripts whose key starts with it, ignoring case,
  most recently active first, up to limit. The CLI asks for 11 so it can say "more than 10". A prefix shorter than 4
  characters is refused, because it would match a large share of every session rather than look one up. The prefix is
  matched literally, so an underscore in it is not a wildcard.

  Background:
    Given a running registry
    And the users "alice" and "bob" are signed in
    And alice has backed up sessions with these keys
      | key                                  |
      | 7d3c9a52-1f4e-4b8a-9c0d-2e6f8a1b3c4d |
      | 7d3cbb10-2a5f-4c9b-8d1e-3f7a9b2c4d5e |
      | 7d3c_imported                        |
      | a1b2c3d4-5e6f-4a7b-8c9d-0e1f2a3b4c5d |
    And bob has backed up a session with the key "7d3c0000-0000-4000-8000-000000000000"

  Scenario: A unique prefix finds one session, whatever its case
    When alice looks up the key prefix "A1B2C3"
    Then she finds exactly these sessions
      | key                                  |
      | a1b2c3d4-5e6f-4a7b-8c9d-0e1f2a3b4c5d |

  Scenario: An ambiguous prefix finds every match, and only hers
    When alice looks up the key prefix "7d3c" with a limit of 11
    Then she finds exactly these sessions
      | key                                  |
      | 7d3c9a52-1f4e-4b8a-9c0d-2e6f8a1b3c4d |
      | 7d3cbb10-2a5f-4c9b-8d1e-3f7a9b2c4d5e |
      | 7d3c_imported                        |

  Scenario: The limit caps the matches
    When alice looks up the key prefix "7d3c" with a limit of 2
    Then she finds 2 sessions

  Scenario: An underscore in the prefix matches only an underscore
    When alice looks up the key prefix "7d3c_"
    Then she finds exactly these sessions
      | key           |
      | 7d3c_imported |

  Scenario: A prefix nothing starts with finds nothing
    When alice looks up the key prefix "ffff"
    Then she finds no sessions

  Scenario: Another user's prefix never matches alice's sessions
    When bob looks up the key prefix "a1b2"
    Then he finds no sessions
    When bob looks up the key prefix "7d3c"
    Then he finds exactly these sessions
      | key                                  |
      | 7d3c0000-0000-4000-8000-000000000000 |

  Scenario Outline: A prefix too short to look anything up, or one no key could start with, is refused
    When alice looks up the key prefix "<prefix>"
    Then the lookup is refused with 400

    Examples:
      | prefix |
      | 7d3    |
      | 7d3c%  |
      | .7d3c  |
