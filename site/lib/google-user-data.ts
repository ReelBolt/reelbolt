// Google user data. One source of truth for the strings Google's OAuth verification checks: the
// Limited Use sentence (which must be verbatim), the no-AI/ML-training affirmation, the policy it
// links to, and the Drive scope the cloud-drive sync asks for. The privacy policy renders these;
// the test pins them.
export const GOOGLE_LIMITED_USE_STATEMENT =
  "ReelBolt's use and transfer of information received from Google APIs adheres to the Google API " +
  'Services User Data Policy, including the Limited Use requirements.';
// A negative affirmation only: it states what we do not do with Google user data, and must never
// be turned into a claim that we train, sell or advertise with it.
export const GOOGLE_NO_MODEL_TRAINING_STATEMENT =
  'We do not use Google Workspace APIs, or any Google user data received through them, to ' +
  'develop, improve or train non-personalized (generalized) AI or ML models.';
export const GOOGLE_API_SERVICES_USER_DATA_POLICY_URL =
  'https://developers.google.com/terms/api-services-user-data-policy';
export const GOOGLE_DRIVE_READONLY_SCOPE = 'https://www.googleapis.com/auth/drive.readonly';
