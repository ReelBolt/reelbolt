---
title: Publishing, scheduling and performance
sidebar_position: 7
description: Posting a finished video to YouTube Shorts or LinkedIn, now or at a time you choose - which plans include it, connecting an account, the checks before a post goes out, the queue, what to do when a post says Check the channel, the numbers you see afterwards, and how the assistant helps without ever posting on its own.
---

# Publishing, scheduling and performance

You can post a finished video to **YouTube Shorts** or **LinkedIn** from ReelBolt, either right away or at a date and time you choose. Afterwards the **Publishing** page shows how each post is doing. Publishing is included in the **Creator plan and above**; on a plan without it, the **Publish** button explains that and links to the plans.

## Connect an account

Open **Publishing** in the main menu and go to the **Accounts** tab. Each platform your administrator has turned on has a **Connect** button. The platform asks you to sign in and to allow ReelBolt to post for you, then you come back to the tab. There is no password or key to copy.

- Accounts belong to the workspace. Only an **owner or admin** of the workspace can connect or disconnect one. Any member who can edit a project can post that project's videos to the workspace's connected accounts.
- If no platform is listed, your administrator has not turned publishing on yet.
- If a card says **Reconnect needed**, the platform withdrew or expired the permission. Choose **Reconnect**; posts queued for that account wait until you do.
- **Disconnect** deletes ReelBolt's saved access and the saved statistics for that account right away. Posts still queued for it are not sent. Posts already on the platform stay there; ReelBolt never deletes them.

### What each platform shows

- **YouTube.** Until YouTube has approved ReelBolt, every video you publish there stays **private**. The Accounts tab and the Publish dialog both say so. Once YouTube approves ReelBolt, your choice of **Public**, **Unlisted** or **Private** applies.
- **LinkedIn.** A post is **public** or for your **connections only**. LinkedIn has no unlisted or private setting. If you choose nothing, the post goes to connections only; it is public only when you choose **Public**.

## Publish a video

1. On a finished video (the run page, the project outputs or the editor), choose **Publish**.
2. Choose where to post. You can pick more than one account.
3. Fill in the title, the post text and the hashtags. Hashtags keep only letters, digits and underscores. If you already used [Write post text](../assistant/post-text.md), paste its result here.
4. Choose who can see it, and turn on **Label as AI-generated** if it applies. By posting you confirm that you may post the video and that the label is right.
5. Choose **Post now** or **Schedule for later** and pick a date, a time and a time zone. You can schedule up to a year ahead. Daylight saving time is handled for the zone you choose.
6. ReelBolt checks the video against the platform's limits (length, file size, picture shape, text length). A problem is listed with what to change. If the picture shape is wrong, **Re-render as vertical** makes a 9:16 version.
7. Review, then choose **Post now** or **Schedule post**.

Scheduled and sent posts appear on the **Queue** tab, as a list or a month calendar. From there you can **Edit** a scheduled post (text, date, time, time zone), **Cancel post**, or **Try again** after a failure.

## What the queue statuses mean

| Status | Meaning |
|--------|---------|
| Draft | Saved, not scheduled. |
| Scheduled | Waiting for its time. |
| Sending | The video is being uploaded now. |
| Published | It is on the platform. **Open the post** takes you to it. |
| Failed | It did not go out. The reason is written next to it. |
| Cancelled | You cancelled it. It was not sent. |
| Check the channel | ReelBolt could not confirm whether the post went out. |

When a post fires, ReelBolt checks everything again, not only when you scheduled it. A post is **not** sent, and fails with a reason, if:

- the project or the video was deleted, or is on hold;
- the person who scheduled it can no longer edit the project, or the workspace's plan no longer includes publishing;
- the account was disconnected or needs reconnecting;
- the content does not pass the safety check that runs before every publication;
- the service was down and the post is **more than 60 minutes late**. It is not posted at a surprising time; schedule it again.

A platform that is busy or rate-limited is tried again automatically up to three times, a little later each time. A platform that refuses the post, or a permission that was withdrawn, fails it at once.

### When a post says "Check the channel"

If something went wrong at the exact moment the video was being sent, ReelBolt cannot know whether the platform accepted it. It never retries such a post by itself, because a duplicate public post is worse than a missed one. Open the channel and look. If the post is there, nothing more is needed. If it is not there, choose **Try again** and confirm that you checked.

You are notified (in the app and by email, if you have it on in **Settings, Notifications**) when a post is published, when it fails, and when an account needs to be reconnected.

## See how a post is doing

The **Performance** tab shows the posts published in the last 30 days: views, likes, comments, shares and saves, by platform and by post. ReelBolt reads the numbers from the platform about 1 hour, 6 hours, 1 day, 3 days, 7 days, 14 days and 30 days after publishing, then stops. The tab shows the time of the last reading.

A platform that does not provide a number shows **Not available**, not zero. Today YouTube provides views, likes and comments; LinkedIn does not provide numbers for member posts, so a LinkedIn post shows **Not available**.

## Limits

- Each workspace can send up to 300 posts a month.
- The platforms limit how many uploads ReelBolt can make in a day across all customers. If today's share is used up, the post fails with a message saying so; schedule it for another day. YouTube's default limit is small until Google grants a higher one.
- Each platform has its own video limits. YouTube Shorts: up to 3 minutes, vertical or square. LinkedIn: 3 seconds to 30 minutes. The dialog shows the exact numbers that apply.

## The assistant and publishing

The assistant can list your connected accounts and your scheduled posts, and can read how a post performed. It can also **propose** scheduling a post or cancelling a scheduled one. It never posts on its own:

- Every schedule or cancel shows a card that names the **account**, the **platform**, the **project and video**, the **time** and the **whole text**. You approve it or decline it.
- The assistant **asks every time**. Auto mode and "always allow" do not apply to publishing, and there is no spending limit that skips the card.
- Publishing is not available over the connection that other AI tools use (MCP): they can read your accounts, posts and performance, but cannot schedule or cancel.
- Scheduled runs and background work never publish.

## Troubleshooting

| What you see | What to do |
|--------------|------------|
| **Publishing is not available yet** | Your administrator has not connected any platform. |
| **Your plan does not include publishing** | Publishing needs the Creator plan or above. |
| **Reconnect needed** | Choose **Reconnect** on the account. |
| A YouTube video is private | YouTube has not approved ReelBolt yet. You can change the video to public in YouTube Studio. |
| **Needs a fix** in the dialog | Read the listed problem. For picture shape, use **Re-render as vertical**. |
| **Failed**, *missed window* | The service was down at the post's time. Schedule it again. |
| **Check the channel** | See the section above. |
