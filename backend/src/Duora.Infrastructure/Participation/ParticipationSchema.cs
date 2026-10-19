namespace Duora.Infrastructure.Participation;

// SQL-owned tables: snapshots are explicit persistence contracts, never EF serialization of domain internals.
internal static class ParticipationSchema
{
    public const string Create = """
        create schema if not exists events;
        create schema if not exists experiences;
        create table experiences.experience_versions (
          id uuid primary key, content_version integer not null check(content_version>0),
          rules_version integer not null check(rules_version=1), schema_version integer not null check(schema_version=1),
          editorial_state text not null check(editorial_state in ('Draft','Published','Retired')),
          content_en jsonb not null, content_pt jsonb not null, audio_manifest jsonb not null default '[]'::jsonb,
          approved_by uuid references identity.users(id), published_at timestamptz,
          check(octet_length(content_en::text)<=32768 and octet_length(content_pt::text)<=32768),
          check(jsonb_typeof(audio_manifest)='array' and octet_length(audio_manifest::text)<=32768));
        create function experiences.keep_published_content() returns trigger language plpgsql as $$
        begin
          if tg_op='DELETE' then
            if old.editorial_state in ('Published','Retired') then raise exception 'Published content is immutable' using errcode='23514'; end if;
            return old;
          end if;
          if old.editorial_state in ('Published','Retired') and
            (new.editorial_state not in ('Published','Retired') or (old.editorial_state='Retired' and new.editorial_state<>'Retired') or new.content_en is distinct from old.content_en or new.content_pt is distinct from old.content_pt or
             new.audio_manifest is distinct from old.audio_manifest or new.approved_by is distinct from old.approved_by or new.published_at is distinct from old.published_at or
             new.content_version<>old.content_version or new.rules_version<>old.rules_version or new.schema_version<>old.schema_version)
          then raise exception 'Published content is immutable' using errcode='23514'; end if;
          return new;
        end $$;
        create trigger immutable_published_content before update or delete on experiences.experience_versions
          for each row execute function experiences.keep_published_content();
        create table experiences.audio_assets (
          experience_version_id uuid not null references experiences.experience_versions(id),
          asset_id text not null, display_locale text not null check(display_locale in ('en-US','pt-BR')),
          sha256 text not null check(sha256 ~ '^[0-9a-f]{64}$'), content_type text not null check(content_type='audio/wav'),
          bytes bytea not null check(octet_length(bytes) between 46 and 6291456),
          primary key(experience_version_id,asset_id));
        create function experiences.keep_published_audio() returns trigger language plpgsql as $$
        declare version_id uuid;
        begin
          version_id := case when tg_op='DELETE' then old.experience_version_id else new.experience_version_id end;
          if exists(select 1 from experiences.experience_versions where id=version_id and editorial_state in ('Published','Retired'))
            or (tg_op='UPDATE' and exists(select 1 from experiences.experience_versions where id=old.experience_version_id and editorial_state in ('Published','Retired')))
          then raise exception 'Published audio is immutable' using errcode='23514'; end if;
          if tg_op='DELETE' then return old; end if;
          return new;
        end $$;
        create trigger immutable_published_audio before insert or update or delete on experiences.audio_assets
          for each row execute function experiences.keep_published_audio();
        create table events.events (
          id uuid primary key, title text not null check(length(title) between 1 and 120),
          state text not null check(state in ('Published','Running','Completed','Cancelled')),
          starts_at timestamptz not null, capacity integer not null check(capacity between 1 and 100),
          confirmed_count integer not null default 0, next_waitlist_position bigint not null default 1,
          experience_version_id uuid not null references experiences.experience_versions(id),
          version bigint not null default 1,
          check(confirmed_count between 0 and capacity));
        create table events.registrations (
          event_id uuid not null references events.events(id), user_id uuid not null references identity.users(id),
          state text not null check(state in ('Waitlisted','Confirmed','CheckedIn','Withdrawn','Completed')),
          waitlist_position bigint, registered_at timestamptz not null, checked_in_at timestamptz,
          primary key(event_id,user_id), check((state='Waitlisted')=(waitlist_position is not null)));
        create unique index ux_registrations_waitlist on events.registrations(event_id,waitlist_position) where state='Waitlisted';
        create table events.rounds (
          id uuid primary key, event_id uuid not null references events.events(id), number integer not null check(number>0),
          state text not null check(state in ('Scheduled','Inviting','Closed','Cancelled')),
          starts_at timestamptz not null, deadline_at timestamptz not null, seed bigint not null,
          algorithm_version integer not null default 2 check(algorithm_version=2), version bigint not null default 1,
          unique(event_id,number), unique(id,event_id));
        create table events.pairings (
          id uuid primary key, round_id uuid not null, event_id uuid not null,
          user_low uuid not null references identity.users(id), user_high uuid not null references identity.users(id),
          algorithm_version integer not null check(algorithm_version=2),
          communication_language text not null check(communication_language in ('en','pt')),
          communication_policy_version integer not null check(communication_policy_version=1),
          created_at timestamptz not null, released_at timestamptz, release_reason text,
          check(user_low<user_high), unique(event_id,user_low,user_high),
          foreign key(round_id,event_id) references events.rounds(id,event_id));
        create table events.pairing_participants (
          pairing_id uuid not null references events.pairings(id), user_id uuid not null references identity.users(id),
          round_id uuid not null references events.rounds(id), released_at timestamptz,
          primary key(pairing_id,user_id), unique(round_id,user_id));
        create unique index ux_pairing_participants_active_user on events.pairing_participants(user_id) where released_at is null;
        create table experiences.game_sessions (
          id uuid primary key, pairing_id uuid not null unique references events.pairings(id),
          experience_version_id uuid not null references experiences.experience_versions(id),
          state text not null check(state in ('Invited','Active','Completed','Aborted','Expired')),
          version bigint not null default 1 check(version>=1), invite_expires_at timestamptz not null,
          game_started_at timestamptz, active_deadline_at timestamptz, public_state jsonb, control_state jsonb,
          check((game_started_at is null)=(public_state is null)),
          check((game_started_at is null)=(control_state is null)),
          check(state<>'Active' or game_started_at is not null),
          check(octet_length(public_state::text)<=32768 and octet_length(control_state::text)<=32768));
        create table experiences.game_participants (
          game_session_id uuid not null references experiences.game_sessions(id), user_id uuid not null references identity.users(id),
          display_locale text not null check(display_locale in ('en-US','pt-BR')),
          accepted_at timestamptz, departed_at timestamptz, private_state jsonb,
          primary key(game_session_id,user_id), check(octet_length(private_state::text)<=32768));
        create table experiences.game_actions (
          game_session_id uuid not null, actor_id uuid not null, action_id uuid not null,
          type text not null, payload_sha256 text not null, expected_version bigint not null, applied_version bigint not null,
          primary key(game_session_id,actor_id,action_id),
          foreign key(game_session_id,actor_id) references experiences.game_participants(game_session_id,user_id),
          check(applied_version>expected_version));
        create table ops.jobs (
          id uuid primary key, kind text not null check(kind in ('build-round','close-round','expire-invite','game-deadline')),
          aggregate_id uuid not null, due_at timestamptz not null, available_at timestamptz not null,
          status text not null default 'Pending' check(status in ('Pending','InFlight','Done','Failed')),
          fencing_token bigint not null default 0, attempts integer not null default 0,
          lease_expires_at timestamptz, completed_at timestamptz, last_error_code text,
          unique(kind,aggregate_id,due_at), check((status='InFlight')=(lease_expires_at is not null)),
          check((status in ('Done','Failed'))=(completed_at is not null)));
        create index ix_jobs_due on ops.jobs(available_at,id) where status='Pending';
        create index ix_jobs_leases on ops.jobs(lease_expires_at) where status='InFlight';
        """;
}
