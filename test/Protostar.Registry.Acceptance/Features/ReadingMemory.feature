Feature: Reading memory redacted never shows a secret's value
  As a developer whose agent reads my memory from another project
  I want the secrets in my memory files redacted in what the agent reads
  So that reading memory never puts a token into an agent's context, while a pull still gets the original

  A memory file is stored exactly as it was pushed, because a pull must write back the original bytes. Downloading
  a project's memory with redacted=true applies the redaction search uses (registry#18) to each file's text, and its
  sha256 and size describe what is served. The fake secret is assembled from parts by the steps.

  Background:
    Given a running registry
    And the user "alice" is signed in
    And alice has backed up a memory file with a GitHub token in it and one without

  Scenario: A redacted read hides the secret and leaves the other file as it was
    When alice reads her memory redacted
    Then the file with the token reads with "[redacted:github-token]" in its place
    And its sha256 and size describe what she reads
    And the file without a secret reads exactly as pushed

  Scenario: The default read is what she pushed, byte for byte
    When alice downloads her memory
    Then both files read exactly as pushed
