---
{
  "document": "privacy",
  "version": "draft-selfie-failures-304",
  "locale": "en-US",
  "controllerName": "Responsável pelo Duora",
  "privacyContact": "support@meetduora.com",
  "approved": false
}
---

# Privacy notice

## Controller and contact

The controller of the personal data processed in Duora is the party responsible for Duora. The contact for privacy requests and for exercising your rights is support@meetduora.com.

## Registration, authentication, and acceptance

Registration processes an email address and password to create and protect an account. The authentication mechanism transforms the password into a hash; it is not public profile information. Confirmation uses the address supplied. Acceptance records bind the document type, version, language actually supplied, and acceptance time. A historical record with an unknown language remains unknown.

## Profile and preferences

Profile features support information and choices such as a display name, biography, optional country and subdivision, communication languages, interface language, and time zone. These choices are independent; language does not establish country, residence, or proficiency. Approximate location, when enabled, is only a correctable suggestion and does not authorize precise geolocation or an individual detection history.

## Participation and visibility

Registrations, rooms, and activities use the information needed for their context. Another person receives only the projection authorized for that feature, not private preference lists or choices that have not been revealed. Photos and presence checks have their own steps. A presence or quality check is not an age check or permission to collect an identity document.

## Operations and providers

The service and its data, including photos, are hosted on a server contracted from Hostinger in Boston, United States. Backups are encrypted before they leave the server and are stored in Backblaze B2, in the eastern United States region; that provider does not receive the encryption keys. Messages sent to support are received by Zoho Mail and forwarded to a Gmail inbox of the responsible party. The selfie presence check is processed by Amazon Web Services (AWS) in the us-east-1 region, in the United States. For people participating from Brazil, this processing takes place outside the country.

Transactional emails (registration confirmation, password reset and account notices) are sent through Resend (Resend, Inc., United States), from the meetduora.com domain, through the sa-east-1 (São Paulo) sending region. Resend processes the recipient's address and the message content only to deliver it. Open and click tracking are turned off.

## Selfie presence check

The selfie presence check uses Amazon Rekognition Face Liveness, from AWS, in the us-east-1 region, under the AWS standard data processing addendum. It assesses presence and capture quality; it does not verify age or compare your identity with another person's identity. Duora does not keep images or videos from the selfie, facial templates, or provider scores.

The approved result, including its state, policy version, and verification dates, is kept for as long as the account exists. Records of attempts that are not approved are deleted by the cleanup process after 30 days from completion or, if not completed, from expiration. The provider session reference and local feedback code are deleted by the cleanup process after 24 hours from expiration.

Separately, to enforce rejection limits and prevent deletion of attempt records from resetting those limits, we keep a minimal record of each counted rejection: the account identifier, the attempt identifier, and the time of rejection. This record contains no image, video, score, feedback, or provider reason. It may remain for more than 30 days and does not expire merely with the passage of time: it is kept until the selfie is approved. Minimal rejection records are deleted when the selfie is approved or when a moderator lifts the suspension for selfie rejections. Physically deleting the account also removes them. Lifting a suspension for another reason does not clear this count.

## Rejection limits and automatic suspension

We count only final rejection results due to capture quality or inconclusive presence, assessed under the same policy version recorded for the attempt. Abandonment, expiration, provider unavailability, technical failures, and results under a different policy version do not count.

After the fifth counted rejection, new attempts are paused for one hour, starting at the time of that fifth rejection on the server. After the pause, there is a second cycle of up to five further rejections. Waiting does not reset the count. The additional limits of five requests per hour and a minimum interval of one minute between requests still apply.

At the tenth counted rejection, new attempts are refused and the system automatically requests account suspension for selfie rejections, without a prior human decision. Suspension is processed afterward and may not be immediate. If a suspension for another reason is already in place, the request waits for that measure to end and proceeds only if the rejection count is still exhausted. While your account is suspended, you cannot participate in events, rooms, or conversations. This suspension has no expiration date and requires a moderator to lift it.

## Requesting human review

You can request a human review of the automatic suspension for selfie rejections by writing to support@meetduora.com, including if you cannot access your account. Requesting a review of this measure is not yet available in the app. Before acting, we will ask for identity verification proportionate to the request.

A moderator can lift the suspension after reviewing it. In that case, the minimal rejection records are deleted and the account returns to the first cycle of attempts. Lifting the suspension does not approve the selfie: the other participation requirements still apply. Requesting a review does not, by itself, remove the suspension or reset the count.

## Retention and requests

Information is retained for as long as each feature needs it for its purpose. Backups follow their own cycle; information deleted from the service may remain in them until the backup expires. To request access to, correction of, or deletion of your information, write to support@meetduora.com; before acting, we will ask for identity verification proportionate to the request.

## Changes to this notice

Changes to this notice create a new version, identified by date and language. Previous versions remain available at their own version address.
