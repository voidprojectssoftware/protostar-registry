Feature: Reading a session never shows a secret's value
  As a developer reading back a session, or an agent reading it for me
  I want the secrets I pasted into it redacted in what I read
  So that reading a session never puts a token or a key on my screen or into an agent's context, while my backup keeps the original

  A transcript is stored exactly as it was uploaded, because backups, restores and verification need the original
  bytes. Reading it with redacted=true applies the same redaction search uses (registry#18), line by line, inside
  JSON string values, so every line is still JSON. The fake secrets are assembled from parts by the steps, so no
  literal sits in the repository for a secret scanner to flag.

  Background:
    Given a running registry
    And the user "alice" is signed in
    And alice has backed up a session with a GitHub token in her prompt, an AWS key in a tool's input, and a password in a tool's JSON output

  Scenario: A redacted read hides every secret and keeps every line valid JSON
    When alice reads the session redacted
    Then every line of what she reads is valid JSON
    And what she reads shows "[redacted:github-token]", "[redacted:aws-access-key]" and "[redacted:secret]"
    And what she reads contains none of the secrets
    And the line without a secret reads exactly as uploaded

  Scenario: The default read is the upload, byte for byte
    When alice downloads the session
    Then what she reads is byte-identical to what she uploaded

  Scenario: A redacted read hides secrets nested in strings, multi-word values, and the other shapes agents write
    Given alice has backed up a session with a password in a curl body, an API key in code, a passphrase, a password with a semicolon, a Kubernetes secret, a bearer token and a GitHub token followed by an underscore
    When alice reads the session redacted
    Then every line of what she reads is valid JSON
    And what she reads shows "[redacted:secret]", "[redacted:bearer-token]" and "[redacted:github-token]"
    And what she reads contains none of the secrets
