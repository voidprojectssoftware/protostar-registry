Feature: Search finds what was said, never a secret's value
  As a developer looking for something I discussed or wrote down
  I want to search my sessions and memory by phrase
  So that I find it again, without the index ever holding a secret I pasted

  Secrets are redacted before text is indexed, so the index and its snippets hold a marker such as
  [redacted:github-token] instead of the value (registry#67). {github-token} stands for a well-formed GitHub
  token, assembled from parts by the steps, so the literal never sits in the repository for a secret scanner to flag.

  Background:
    Given a running registry
    And the user "alice" is signed in

  Scenario Outline: A phrase in a <kind> is found, a secret beside it is not
    Given alice has backed up a <kind> saying "the aurora borealis report uses token {github-token} to publish"
    When alice searches for '"aurora borealis"'
    Then the search finds her <kind>
    And the snippet marks "aurora" and "borealis" as matches
    And the snippet shows "[redacted:github-token]"
    And the snippet does not contain "{github-token}"
    When alice searches for "{github-token}"
    Then the search finds nothing

    Examples:
      | kind    |
      | session |
      | memory  |
