---
title: AudioSchedulerService — Voice Audio Scheduler Service Design
version: 1.0
date_created: 2025-09-13
last_updated: 2025-09-13
owner: voice_chat_demo maintainers
tags: [design, audio, pipeline]
---

## 1. Goal

This service responsible for timed scheduling of AudioEvent so that they passed to next stage in pipeline according to duration of audio, 
so audio can be interrupted when turn is interrupted.

## 2. Definitions

1. AudioEvent: Pipeline unit containing PCM audio bytes as well as turn id and transcript.
3. Turn: Conversation context managed by TurnManager; interruption triggers audio preemption.

## 3. Requirements

1. Queue & Sequencing: Accept enqueue of AudioEvents to internal queue and emit AudioEvents according to their duration
3. Interruption: subscribe to TurnManager to track cancelled turns, so they are discarded.
