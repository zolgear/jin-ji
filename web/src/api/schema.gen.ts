// このファイルは scripts/gen-api-types.mjs が openapi/v1.json から生成する。手で編集しない（pnpm gen:api で再生成）。
// jin-ji API v1
/* eslint-disable */

export interface components {
  schemas: {
    AddCimdDomainRequest: {
      domain: string | null;
    };
    AddClientKeyRequest: {
      key_id: string | null;
      public_key_pem: string | null;
    };
    AddGroupMemberRequest: {
      person_id?: string | null; // uuid
      role?: "owner" | "lead" | "member" | null;
      from?: string | null; // date
      until?: string | null; // date
    };
    AgentSuggestionResponse: {
      id: string; // uuid
      resolution: "select_candidate" | "register_new" | "refer_to_correction" | "reject";
      person_id: string | null; // uuid
      org_unit_id: string | null; // uuid
      confidence: number; // double
      rationale: string;
      suggested_by_person_id: string; // uuid
      suggested_via_client_id: string;
      created_at: string; // date-time
    };
    ApprovalOrgUnitResponse: {
      id: string; // uuid
      legal_entity_id: string; // uuid
      code: string;
      name: string;
      level_key: string | null;
      level_name: string | null;
      level_rank: number | null; // int32
    };
    ApprovalRouteResponse: {
      person_id: string; // uuid
      as_of: string; // date
      vacancy: "promote" | "vacant";
      start: components["schemas"]["ApprovalStartResponse"] | null;
      steps: components["schemas"]["ApprovalStepResponse"][];
      unresolved_units: components["schemas"]["ApprovalSkippedUnitResponse"][];
    };
    ApprovalSkippedUnitResponse: {
      org_unit: components["schemas"]["ApprovalOrgUnitResponse"];
      reason: "vacant" | "self";
    };
    ApprovalStartResponse: {
      org_unit: components["schemas"]["ApprovalOrgUnitResponse"];
      assignment_type: "primary" | "concurrent";
      secondment: "none" | "outbound" | "inbound";
      position_title: string | null;
    };
    ApprovalStepResponse: {
      order: number; // int32
      org_unit: components["schemas"]["ApprovalOrgUnitResponse"];
      status: "assigned" | "vacant";
      approver: components["schemas"]["PersonResponse"] | null;
      approver_position: string | null;
      skipped_units: components["schemas"]["ApprovalSkippedUnitResponse"][];
    };
    AssignmentResponse: {
      org_unit_id: string; // uuid
      org_unit_code: string;
      org_unit_name: string;
      legal_entity_id: string; // uuid
      type: "primary" | "concurrent";
      position_title: string | null;
      secondment: "none" | "outbound" | "inbound";
      valid_from: string; // date
      valid_until: string | null; // date
    };
    BuildingDetailResponse: {
      as_of: string; // date
      building: components["schemas"]["BuildingResponse"];
    };
    BuildingListResponse: {
      as_of: string; // date
      site_id: string; // uuid
      items: components["schemas"]["BuildingResponse"][];
    };
    BuildingResponse: {
      id: string; // uuid
      site_id: string; // uuid
      site_code: string | null;
      site_name: string | null;
      code: string;
      name: string;
      sort_order: number; // int32
      floors: components["schemas"]["FloorSummaryResponse"][];
    };
    CandidateResponse: {
      person_id: string; // uuid
      rule: "employee_number" | "work_email" | "name_and_org";
      confidence: number; // double
    };
    ChangeGroupRoleRequest: {
      role?: "owner" | "lead" | "member" | null;
    };
    ChangeListResponse: {
      items: components["schemas"]["ChangeResponse"][];
      next_cursor: string;
      has_more: boolean;
    };
    ChangeResponse: {
      type: "person" | "org_unit" | "site" | "group";
      id: string; // uuid
    };
    CimdDomainsResponse: {
      configured: string[];
      added: string[];
    };
    ClientSecretResponse: {
      id: string; // uuid
      secret: string;
      expires_at: string; // date-time
    };
    ClientStatusResponse: {
      client_id: string;
      state: "normal" | "throttled" | "suspended";
      rule: string | null;
      since: string | null; // date-time
      until: string | null; // date-time
    };
    CloseCorrectionRequest: {
      status?: "resolved" | "dismissed" | null;
      note?: string | null;
    };
    CloseGroupRequest: {
      date?: string | null; // date
    };
    ConsentClientResponse: {
      client_id: string;
      name: string;
      registration: "registered" | "cimd";
      loopback_only: boolean;
      fields: string[];
      mcp_tools: string[];
    };
    ConsentDecisionRequest: {
      approve: boolean | null;
    };
    ConsentDecisionResponse: {
      redirect_to: string;
    };
    ConsentResponse: {
      client: components["schemas"]["ConsentClientResponse"];
      redirect_uri: string;
      redirect_host: string;
      resource: "api" | "mcp";
      resource_uri: string;
      scopes: string[];
      expires_at: string; // date-time
    };
    CorrectionListResponse: {
      items: components["schemas"]["CorrectionResponse"][];
      people: components["schemas"]["CorrectionPersonResponse"][];
      next_cursor: string | null;
    };
    CorrectionPersonResponse: {
      id: string; // uuid
      display_name: string | null;
      employee_number: string | null;
      legal_entity_name: string | null;
      primary_org_name: string | null;
    };
    CorrectionResponse: {
      id: string; // uuid
      status: "open" | "resolved" | "dismissed" | "auto_resolved" | "superseded";
      origin: "ingest" | "user" | "agent";
      issue: "missing_employee_number" | "missing_required" | "invalid_date" | "invalid_value" | "duplicate_row" | "unknown_legal_entity" | "unknown_org" | "unknown_org_level" | "unknown_head" | "conflicting_identity" | "suspected_sensitive_value" | "abolished_org" | "stale_head" | "uninterpretable_action" | "suspected_employee_number_typo" | "referred_from_review" | "reported_value" | "unknown_site" | "unknown_building" | "unknown_floor" | "unknown_seat" | "unknown_zone" | "site_mismatch";
      message: string;
      person_id: string | null; // uuid
      field: string | null;
      source_field: string | null;
      reason: string | null;
      suggested_value: string | null;
      batch_id: string | null; // uuid
      file_name: string | null;
      source_row_number: number | null; // int32
      reported_by_person_id: string | null; // uuid
      reported_via_client_id: string | null;
      reported_by_me: boolean;
      endorsed_by_me: boolean;
      about_me: boolean;
      endorsements: number; // int32
      created_at: string; // date-time
      age_days: number; // int32
      closed_at: string | null; // date-time
      closed_kind: "person" | "auto" | null;
      closed_by_person_id: string | null; // uuid
      closed_note: string | null;
    };
    CorrectionSummaryResponse: {
      open: number; // int32
      new: number; // int32
      stale: number; // int32
      new_within_days: number; // int32
      stale_after_days: number; // int32
      oldest_created_at: string | null; // date-time
    };
    CreateCorrectionRequest: {
      person_id?: string | null; // uuid
      field?: "employee_number" | "legal_entity" | "display_name" | "family_name" | "given_name" | "family_name_kana" | "given_name_kana" | "family_name_latin" | "given_name_latin" | "work_email" | "extension_number" | "work_mobile_phone" | "site" | "remote_work" | "workplace" | "seat" | "employment_type" | "job_type" | "hire_date" | "termination_date" | "employment_status" | "account_status" | "account_suspension" | "assignments" | "managers" | "personnel_actions" | "groups" | null;
      reason?: string | null;
      suggested_value?: string | null;
    };
    CreateCorrectionResponse: {
      outcome: "created" | "endorsed" | "already_reported";
      correction: components["schemas"]["CorrectionResponse"];
    };
    CreateFieldRegistrationRequest: {
      effective_date?: string | null; // date
      note?: string | null;
      changes?: components["schemas"]["FieldChangeRequest"][] | null;
    };
    CreateGroupRequest: {
      name?: string | null;
      kind?: "team" | "project" | "committee" | "other" | null;
      owner_org_unit_id?: string | null; // uuid
      visibility?: "all" | "members" | null;
      valid_from?: string | null; // date
    };
    CreateLoginBindingRequest: {
      person_id: string; // uuid
      unbound_login_id: string | null; // uuid
      provider: string | null;
      subject: string | null;
    };
    CsrfResponse: {
      token: string;
      header_name: string;
    };
    DelegationResponse: {
      id: string; // uuid
      client_id: string;
      client_name: string | null;
      registration: "registered" | "cimd";
      resource: "api" | "mcp";
      scopes: string[];
      granted_at: string; // date-time
      last_used_at: string | null; // date-time
      expires_at: string; // date-time
    };
    DisableAccountResponse: {
      sessions_ended: number; // int32
      delegations_revoked: number; // int32
    };
    DisclosureLeadResponse: {
      id: string; // uuid
      client_ref: string; // uuid
      lead_days: number; // int32
      status: "proposed" | "approved" | "rejected" | "superseded";
      note: string | null;
      proposed_by_kind: "person" | "cli";
      proposed_by: string; // uuid
      proposed_at: string; // date-time
      decided_by: string | null; // uuid
      decided_at: string | null; // date-time
      superseded_at: string | null; // date-time
    };
    EnableTwoFactorRequest: {
      code: string | null;
    };
    FacetValueResponse: {
      value: string;
      label: string;
      count: number; // int32
    };
    FieldChangeRequest: {
      kind?: "assignment" | "person" | "org_update" | "org_create" | "org_abolish" | null;
      target_id?: string | null; // uuid
      values?: {
        [key: string]: string;
      } | null;
    };
    FieldGrantResponse: {
      id: string; // uuid
      org_unit_id: string; // uuid
      person_id: string; // uuid
      from: string; // date
      until: string; // date
      granted_by: string; // uuid
      granted_at: string; // date-time
      revoked_by: string | null; // uuid
      revoked_at: string | null; // date-time
    };
    FieldModeResponse: {
      domain: "org_structure" | "org_site" | "assignment" | "person_site" | "person_contact";
      mode: "hr" | "field" | "both";
    };
    FieldRegistrationChangeResponse: {
      id: string; // uuid
      target_kind: "person" | "person_version" | "org_unit" | "org_unit_version" | "assignment" | "personnel_action" | "account_suspension" | "external_identity" | "site" | "site_version" | "building" | "building_version" | "floor" | "floor_version" | "floor_layout" | "workplace_assignment";
      operation: "create" | "correct" | "change" | "review" | "supplement" | "abolish" | "end";
      target_id: string | null; // uuid
      status: "proposed" | "needs_review" | "applied" | "rejected" | "resolved";
      payload: {
        [key: string]: string;
      };
      diff: {
        [key: string]: components["schemas"]["ProposalDiffResponse"];
      };
    };
    FieldRegistrationResponse: {
      id: string; // uuid
      status: "draft" | "pending_consent" | "applied" | "withdrawn" | "discarded" | "expired";
      effective_date: string; // date
      note: string | null;
      registered_by: string; // uuid
      registered_via: "user" | "agent";
      created_at: string; // date-time
      expires_at: string; // date-time
      org_unit_ids: string[];
      person_ids: string[];
      consent_org_unit_ids: string[];
      consented_by: string | null; // uuid
      consented_at: string | null; // date-time
      applied_by: string | null; // uuid
      applied_at: string | null; // date-time
      withdrawn_by: string | null; // uuid
      withdrawn_at: string | null; // date-time
      changes: components["schemas"]["FieldRegistrationChangeResponse"][] | null;
    };
    FieldScopeResponse: {
      manages_all: boolean;
      org_unit_ids: string[];
      parent_scope_org_unit_ids: string[];
      grantable_org_unit_ids: string[];
    };
    FieldSettingResponse: {
      id: string; // uuid
      domain: "org_structure" | "org_site" | "assignment" | "person_site" | "person_contact";
      mode: "hr" | "field" | "both";
      status: "proposed" | "approved" | "rejected" | "superseded";
      note: string | null;
      proposed_by: string; // uuid
      proposed_at: string; // date-time
      decided_by: string | null; // uuid
      decided_at: string | null; // date-time
    };
    FixtureResponse: {
      kind: "core" | "room" | "void";
      name: string | null;
      polygon: number[][];
    };
    FloorLayoutResponse: {
      as_of: string; // date
      floor: components["schemas"]["FloorRefResponse"];
      layout: components["schemas"]["LayoutResponse"] | null;
    };
    FloorOccupantListResponse: {
      as_of: string; // date
      floor_id: string; // uuid
      items: components["schemas"]["FloorOccupantResponse"][];
      next_cursor: string | null;
    };
    FloorOccupantResponse: {
      person: components["schemas"]["PersonResponse"];
      seat_id: string | null; // uuid
      seat_code: string | null;
      zone_key: string | null; // uuid
      zone_name: string | null;
      role: "primary" | "secondary";
      source: "seat" | "zone";
    };
    FloorPresenceResponse: {
      date: string; // date
      floor: components["schemas"]["FloorRefResponse"];
      seats: components["schemas"]["PresenceSeatResponse"][];
      items: components["schemas"]["PresenceItemResponse"][];
    };
    FloorRefResponse: {
      id: string; // uuid
      building_id: string; // uuid
      building_code: string;
      building_name: string;
      site_id: string; // uuid
      label: string;
      level_order: number; // int32
      kind: "office" | "factory" | "warehouse" | "lobby" | "service" | "other";
    };
    FloorSummaryResponse: {
      id: string; // uuid
      building_id: string; // uuid
      label: string;
      level_order: number; // int32
      kind: "office" | "factory" | "warehouse" | "lobby" | "service" | "other";
      layout_id: string | null; // uuid
      layout_valid_from: string | null; // date
      seat_count: number; // int32
      occupied_count: number; // int32
      vacant_count: number; // int32
      unavailable_count: number; // int32
    };
    GrantFieldEditorRequest: {
      org_unit_id?: string | null; // uuid
      person_id?: string | null; // uuid
      from?: string | null; // date
      until?: string | null; // date
    };
    GroupDetailResponse: {
      group: components["schemas"]["GroupResponse"];
      members: components["schemas"]["GroupMemberResponse"][];
    };
    GroupMemberResponse: {
      person_id: string; // uuid
      role: "owner" | "lead" | "member";
      from: string; // date
      until: string | null; // date
    };
    GroupMembershipResponse: {
      group_id: string; // uuid
      name: string;
      kind: "team" | "project" | "committee" | "other";
      role: "owner" | "lead" | "member";
      managed: boolean;
    };
    GroupResponse: {
      id: string; // uuid
      name: string;
      kind: "team" | "project" | "committee" | "other";
      owner_org_unit_id: string | null; // uuid
      visibility: "all" | "members";
      managed: boolean;
      valid_from: string; // date
      valid_until: string | null; // date
      member_count: number; // int32
    };
    GroupSettingsRequest: {
      visibility?: "all" | "members" | null;
      managed?: boolean | null;
    };
    IssueClientSecretRequest: {
      lifetime_days: number | null; // int32
    };
    IssueResetLinkRequest: {
      purpose: string | null;
    };
    JsonElement: unknown;
    LayoutResponse: {
      id: string; // uuid
      valid_from: string; // date
      grid_mm: number; // int32
      outline: number[][];
      fixtures: components["schemas"]["FixtureResponse"][];
      zones: components["schemas"]["ZoneResponse"][];
      seats: components["schemas"]["LayoutSeatResponse"][];
    };
    LayoutSeatResponse: {
      seat_id: string; // uuid
      code: string;
      zone_key: string | null; // uuid
      x_mm: number; // int32
      y_mm: number; // int32
      rotation_deg: number; // int32
      desk_type: "desk_1000" | "desk_1200" | "desk_1400" | "desk_1600" | "desk_1800" | "station" | "counter" | "booth";
      kind: "fixed" | "station";
      capacity: number; // int32
      features: ("monitor" | "dual_monitor" | "standing" | "phone_booth" | "docking_station" | "locker" | "quiet")[];
      status: "vacant" | "occupied" | "unavailable";
    };
    LegalEntityLevelsResponse: {
      id: string; // uuid
      code: string;
      name: string;
      uses_default_levels: boolean;
      levels: components["schemas"]["OrgLevelResponse"][];
    };
    LegalEntityRef: {
      id: string; // uuid
      code: string | null;
      name: string | null;
    };
    LegalEntityResponse: {
      id: string; // uuid
      code: string;
      name: string;
    };
    LlmRankResponse: {
      id: string; // uuid
      rank: number; // int32
      confidence: number; // double
      reason: string;
    };
    LlmSuggestionResponse: {
      kind: string;
      model: string;
      endpoint: string;
      people: components["schemas"]["LlmRankResponse"][];
      orgs: components["schemas"]["LlmRankResponse"][];
      discarded_ids: number; // int32
    };
    LocalAccountResponse: {
      id: string; // uuid
      kind: "person" | "break_glass";
      person_id: string | null; // uuid
      user_name: string;
      has_password: boolean;
      two_factor_enabled: boolean;
      passkey_count: number; // int32
      lockout_end: string | null; // date-time
      disabled_at: string | null; // date-time
      created_at: string; // date-time
    };
    LoginBindingResponse: {
      id: string; // uuid
      provider: string;
      issuer: string;
      subject: string;
      person_id: string; // uuid
      basis: "entra_object_id" | "employee_number" | "manual";
      created_at: string; // date-time
    };
    LoginMethodsResponse: {
      providers: components["schemas"]["LoginProviderResponse"][];
      password: boolean;
      passkey: boolean;
    };
    LoginProviderResponse: {
      name: string;
      display_name: string;
      type: "entra" | "oidc";
      login_url: string;
    };
    LoginResponse: {
      status: "signed_in" | "two_factor_required";
      session: components["schemas"]["SessionResponse"] | null;
      csrf_token: string | null;
    };
    ManagerResponse: {
      person_id: string; // uuid
      managers: components["schemas"]["PersonResponse"][];
    };
    MetaResponse: {
      business_date: string; // date
      time_zone: string;
      can_view_future: boolean;
      max_as_of: string | null; // date
      legal_entities: components["schemas"]["LegalEntityLevelsResponse"][];
      default_levels: components["schemas"]["OrgLevelResponse"][];
      milestones: components["schemas"]["MilestoneResponse"][];
    };
    MilestoneResponse: {
      date: string; // date
      kinds: ("org_change" | "mass_action")[];
    };
    NameVersionResponse: {
      valid_from: string; // date
      valid_until: string | null; // date
      family_name: string;
      given_name: string | null;
      family_name_kana: string | null;
      given_name_kana: string | null;
      display_name: string;
    };
    OAuthClientResponse: {
      id: string; // uuid
      client_id: string;
      registration: "registered" | "cimd";
      status: "active" | "pending_approval" | "suspended" | "rejected";
      display_name: string | null;
      auth_method: "none" | "private_key_jwt";
      grant_types: string[];
      redirect_uris: string[];
      resources: string[];
      scopes: string[];
      fields: string[];
      mcp_tools: string[];
      rate_limit_per_client: number | null; // int32
      metadata_expires_at: string | null; // date-time
      created_at: string; // date-time
      disclosure_lead_days: number; // int32
    };
    OrgHeadcountResponse: {
      org_unit_id: string; // uuid
      primary: number; // int32
      primary_total: number; // int32
      concurrent: number; // int32
      secondment: number; // int32
    };
    OrgLevelResponse: {
      key: string;
      name: string;
      rank: number; // int32
    };
    OrgLinkResponse: {
      from_org_unit_id: string; // uuid
      to_org_unit_id: string; // uuid
      kind: "concurrent" | "secondment";
      count: number; // int32
    };
    OrgSearchResponse: {
      as_of: string; // date
      items: components["schemas"]["OrgUnitResponse"][];
      has_more: boolean;
    };
    OrgStatsResponse: {
      as_of: string; // date
      units: components["schemas"]["OrgHeadcountResponse"][];
      links: components["schemas"]["OrgLinkResponse"][];
    };
    OrgTreeResponse: {
      as_of: string; // date
      units: components["schemas"]["OrgUnitResponse"][];
    };
    OrgUnitResponse: {
      id: string; // uuid
      legal_entity: components["schemas"]["LegalEntityRef"];
      code: string;
      name: string;
      short_name: string | null;
      level: components["schemas"]["OrgLevelResponse"] | null;
      parent_id: string | null; // uuid
      head_person_id: string | null; // uuid
      site: components["schemas"]["SiteRefResponse"] | null;
    };
    PasskeyCredentialRequest: {
      credential: components["schemas"]["JsonElement"];
      name: string | null;
    };
    PasskeyResponse: {
      id: string;
      name: string | null;
      created_at: string; // date-time
      is_backed_up: boolean;
    };
    PasswordLoginRequest: {
      user_name: string | null;
      password: string | null;
    };
    PersonFacetsResponse: {
      legal_entities: components["schemas"]["FacetValueResponse"][];
      levels: components["schemas"]["FacetValueResponse"][];
      assignment_kinds: components["schemas"]["FacetValueResponse"][];
      positions: components["schemas"]["FacetValueResponse"][];
      employment_types: components["schemas"]["FacetValueResponse"][];
      job_types: components["schemas"]["FacetValueResponse"][];
      sites: components["schemas"]["FacetValueResponse"][];
      site_types: components["schemas"]["FacetValueResponse"][];
      prefectures: components["schemas"]["FacetValueResponse"][];
      remote_work: components["schemas"]["FacetValueResponse"][];
      employment_statuses: components["schemas"]["FacetValueResponse"][];
      account_statuses: components["schemas"]["FacetValueResponse"][];
    };
    PersonListResponse: {
      items: components["schemas"]["PersonResponse"][];
      next_cursor: string | null;
    };
    PersonResponse: {
      id: string; // uuid
      visible_fields: string[];
      employee_number: string | null;
      legal_entity: components["schemas"]["LegalEntityResponse"] | null;
      display_name: string | null;
      family_name: string | null;
      given_name: string | null;
      family_name_kana: string | null;
      given_name_kana: string | null;
      family_name_latin: string | null;
      given_name_latin: string | null;
      work_email: string | null;
      extension_number: string | null;
      work_mobile_phone: string | null;
      site: components["schemas"]["SiteRefResponse"] | null;
      remote_work: boolean | null;
      workplace: components["schemas"]["WorkplaceRefResponse"] | null;
      seat: components["schemas"]["SeatRefResponse"] | null;
      presence: components["schemas"]["PresenceRefResponse"] | null;
      employment_type: string | null;
      job_type: string | null;
      hire_date: string | null; // date
      termination_date: string | null; // date
      employment_status: "active" | "terminated" | null;
      account_status: "active" | "suspended" | null;
      account_suspension: components["schemas"]["SuspensionResponse"] | null;
      assignments: components["schemas"]["AssignmentResponse"][] | null;
      managers: string[] | null;
      personnel_actions: components["schemas"]["PersonnelActionResponse"][] | null;
      groups: components["schemas"]["GroupMembershipResponse"][] | null;
      name_history: components["schemas"]["NameVersionResponse"][] | null;
    };
    PersonSearchResponse: {
      items: components["schemas"]["PersonResponse"][];
      next_cursor: string | null;
      total: number | null; // int32
      facets: components["schemas"]["PersonFacetsResponse"] | null;
    };
    PersonnelActionResponse: {
      id: string; // uuid
      action_type: "hire" | "termination" | "transfer" | "position_appointment" | "position_dismissal" | "concurrent_start" | "concurrent_end" | "secondment_start" | "secondment_end" | "suspension_start" | "suspension_end";
      effective_date: string; // date
      org_unit_id: string | null; // uuid
      assignment_type: "primary" | "concurrent" | null;
      position_title: string | null;
      document_number: string | null;
    };
    PresenceItemResponse: {
      person: components["schemas"]["PersonResponse"];
      seat_id: string | null; // uuid
      seat_code: string | null;
      zone_key: string | null; // uuid
      zone_name: string | null;
    };
    PresenceRefResponse: {
      date: string; // date
      building_id: string; // uuid
      building_name: string;
      floor_id: string; // uuid
      floor_label: string;
      zone_key: string | null; // uuid
      zone_name: string | null;
      seat_id: string | null; // uuid
      seat_code: string | null;
    };
    PresenceSeatResponse: {
      seat_id: string; // uuid
      code: string;
      status: "occupied" | "unavailable";
    };
    ProblemDetails: {
      type?: string | null;
      title?: string | null;
      status?: number | null; // int32
      detail?: string | null;
      instance?: string | null;
    };
    ProposalDiffResponse: {
      old: string | null;
      new: string | null;
    };
    ProposalResponse: {
      id: string; // uuid
      batch_id: string; // uuid
      target_kind: "person" | "person_version" | "org_unit" | "org_unit_version" | "assignment" | "personnel_action" | "account_suspension" | "external_identity" | "site" | "site_version" | "building" | "building_version" | "floor" | "floor_version" | "floor_layout" | "workplace_assignment";
      operation: "create" | "correct" | "change" | "review" | "supplement" | "abolish" | "end";
      status: "proposed" | "needs_review" | "applied" | "rejected" | "resolved";
      target_id: string | null; // uuid
      effective_date: string | null; // date
      code: string | null;
      note: string | null;
      row_numbers: number[];
      confidence: number; // double
      candidates: components["schemas"]["CandidateResponse"][];
      org_candidates: string[];
      llm: components["schemas"]["LlmSuggestionResponse"] | null;
      resolution: "selected_candidate" | "registered_new" | "referred_to_correction" | null;
      resolved_from_proposal_id: string | null; // uuid
      agent_suggestions: components["schemas"]["AgentSuggestionResponse"][];
    };
    ProposeDisclosureLeadRequest: {
      lead_days: number | null; // int32
      note: string | null;
    };
    ProposeFieldSettingRequest: {
      domain?: "org_structure" | "org_site" | "assignment" | "person_site" | "person_contact" | null;
      mode?: "hr" | "field" | "both" | null;
      note?: string | null;
    };
    RecoveryCodesResponse: {
      recovery_codes: string[];
    };
    RedeliverWebhookRequest: {
      delivery_id: string | null; // uuid
    };
    RegisterClientRequest: {
      client_id: string | null;
      display_name: string | null;
      auth_method: "none" | "private_key_jwt" | null;
      grant_types: string[] | null;
      redirect_uris: string[] | null;
      resources: string[] | null;
      scopes: string[] | null;
      fields: string[] | null;
      mcp_tools: string[] | null;
      rate_limit_per_client: number | null; // int32
    };
    RegisterWebhookRequest: {
      url: string | null;
      max_events_per_delivery: number | null; // int32
      summary_threshold: number | null; // int32
      min_interval_seconds: number | null; // int32
    };
    ResetLinkResponse: {
      link_id: string; // uuid
      account_id: string; // uuid
      purpose: "password" | "passkey";
      url: string | null;
      token: string;
      expires_at: string; // date-time
    };
    ResetPasskeyOptionsRequest: {
      token: string | null;
    };
    ResetPasskeyRequest: {
      token: string | null;
      credential: components["schemas"]["JsonElement"];
      name: string | null;
    };
    ResetPasswordRequest: {
      token: string | null;
      new_password: string | null;
    };
    ResolveProposalRequest: {
      resolution?: string | null;
      person_id?: string | null; // uuid
      org_unit_id?: string | null; // uuid
    };
    ResolveProposalResponse: {
      proposal_id: string; // uuid
      status: "proposed" | "needs_review" | "applied" | "rejected" | "resolved";
      resolution: "selected_candidate" | "registered_new" | "referred_to_correction" | null;
      derived_proposal_ids: string[];
      correction_id: string | null; // uuid
    };
    RevokeClientTokensRequest: {
      person_id: string | null; // uuid
    };
    RevokedTokensResponse: {
      revoked: number; // int32
    };
    RotateWebhookSecretRequest: {
      overlap_hours: number | null; // int32
    };
    SeatRefResponse: {
      seat_id: string; // uuid
      code: string;
      building_id: string; // uuid
      building_name: string;
      floor_id: string; // uuid
      floor_label: string;
      zone_key: string | null; // uuid
      zone_name: string | null;
      x_mm: number; // int32
      y_mm: number; // int32
      rotation_deg: number; // int32
      kind: "fixed" | "station";
    };
    SeatSearchItemResponse: {
      seat_id: string; // uuid
      code: string;
      building_id: string; // uuid
      building_name: string;
      floor_id: string; // uuid
      floor_label: string;
      zone_key: string | null; // uuid
      zone_name: string | null;
      kind: "fixed" | "station";
      capacity: number; // int32
      features: ("monitor" | "dual_monitor" | "standing" | "phone_booth" | "docking_station" | "locker" | "quiet")[];
      status: "vacant" | "occupied" | "unavailable";
    };
    SeatSearchResponse: {
      as_of: string; // date
      items: components["schemas"]["SeatSearchItemResponse"][];
      has_more: boolean;
    };
    SessionResponse: {
      kind: "person" | "break_glass";
      person_id: string | null; // uuid
      account_id: string | null; // uuid
      session_id: string; // uuid
      provider: string;
      methods: string[];
      roles: string[];
    };
    SiteListResponse: {
      as_of: string; // date
      items: components["schemas"]["SiteResponse"][];
    };
    SiteOrgUnitResponse: {
      id: string; // uuid
      code: string;
      name: string;
    };
    SiteRefResponse: {
      id: string; // uuid
      legal_entity_id: string; // uuid
      code: string;
      name: string;
      type: "head_office" | "branch" | "sales_office" | "factory" | "laboratory" | "logistics" | "store" | "other";
      prefecture_code: string | null;
      prefecture_name: string | null;
      inherited: boolean;
    };
    SiteResponse: {
      id: string; // uuid
      legal_entity: components["schemas"]["LegalEntityRef"];
      code: string;
      name: string;
      type: "head_office" | "branch" | "sales_office" | "factory" | "laboratory" | "logistics" | "store" | "other";
      prefecture_code: string | null;
      prefecture_name: string | null;
      location: string | null;
      member_count: number; // int32
      remote_work_count: number; // int32
      org_units: components["schemas"]["SiteOrgUnitResponse"][];
    };
    SuspensionResponse: {
      from: string; // date
      until: string | null; // date
    };
    SyncChangeResponse: {
      field: string;
      current: string | null;
      desired: string | null;
      current_known: boolean;
    };
    SyncEvidenceResponse: {
      field: string;
      source: string;
      reference: string | null;
      value: string | null;
    };
    SyncPlanDetailResponse: {
      plan: components["schemas"]["SyncPlanResponse"];
      items: components["schemas"]["SyncPlanItemResponse"][];
    };
    SyncPlanItemResponse: {
      id: string; // uuid
      sequence: number; // int32
      priority: "critical" | "high" | "normal" | "info";
      action: "disable_account" | "block_sign_in" | "enable_account" | "update_attributes" | "set_manager" | "create_user" | "unmatched_account" | "excluded_account";
      in_script: boolean;
      person_id: string | null; // uuid
      employee_number: string | null;
      object_id: string | null;
      user_principal_name: string | null;
      changes: components["schemas"]["SyncChangeResponse"][];
      code: string | null;
      basis: components["schemas"]["SyncEvidenceResponse"][];
      note: string | null;
      outcome: "succeeded" | "already_applied" | "skipped_precondition" | "failed" | "would_apply" | null;
    };
    SyncPlanResponse: {
      id: string; // uuid
      target_system: string;
      status: "proposed" | "approved" | "rejected" | "superseded" | "stale" | "applied";
      business_date: string; // date
      proposed_by_person_id: string; // uuid
      proposed_at: string; // date-time
      expires_at: string; // date-time
      decided_by_person_id: string | null; // uuid
      decided_at: string | null; // date-time
      applied_at: string | null; // date-time
      source_file_name: string;
      directory_user_count: number; // int32
      settings_name: string;
      settings_sha256: string;
      critical_count: number; // int32
      summary: {
        [key: string]: number;
      };
    };
    TwoFactorLoginRequest: {
      code: string | null;
      recovery_code: string | null;
    };
    TwoFactorSetupResponse: {
      shared_key: string;
      authenticator_uri: string;
    };
    UnboundLoginResponse: {
      id: string; // uuid
      provider: string;
      issuer: string;
      subject: string;
      tenant_id: string | null;
      employee_number: string | null;
      email: string | null;
      display_name: string | null;
      candidate_person_ids: string[];
      attempt_count: number; // int32
      first_attempted_at: string; // date-time
      last_attempted_at: string; // date-time
      resolved_at: string | null; // date-time
      resolved_binding_id: string | null; // uuid
    };
    UpdateClientRequest: {
      display_name: string | null;
      redirect_uris: string[] | null;
      resources: string[] | null;
      scopes: string[] | null;
      fields: string[] | null;
      mcp_tools: string[] | null;
      rate_limit_per_client: number | null; // int32
      clear_rate_limit: boolean | null;
    };
    UpdateGroupRequest: {
      name?: string | null;
      kind?: "team" | "project" | "committee" | "other" | null;
      owner_org_unit_id?: string | null; // uuid
      clear_owner_org_unit?: boolean | null;
    };
    UpdateWebhookRequest: {
      url: string | null;
      status: "active" | "disabled" | null;
      max_events_per_delivery: number | null; // int32
      summary_threshold: number | null; // int32
      min_interval_seconds: number | null; // int32
    };
    WebhookDeliveryResponse: {
      id: string; // uuid
      kind: "changes" | "summary" | "ping";
      status: "pending" | "sending" | "succeeded" | "dead";
      event_count: number; // int32
      attempts: number; // int32
      next_attempt_at: string; // date-time
      last_attempt_at: string | null; // date-time
      last_http_status: number | null; // int32
      last_error: string | null;
      created_at: string; // date-time
      succeeded_at: string | null; // date-time
      dead_at: string | null; // date-time
      redelivered_by: string | null; // uuid
      redelivered_at: string | null; // date-time
      body: string;
    };
    WebhookPingResponse: {
      delivery_id: string; // uuid
    };
    WebhookRedeliveredResponse: {
      redelivered: number; // int32
    };
    WebhookResponse: {
      id: string; // uuid
      client_ref: string; // uuid
      client_id: string;
      client_name: string | null;
      client_status: "active" | "pending_approval" | "suspended" | "rejected";
      disclosure_lead_days: number; // int32
      url: string;
      status: "active" | "disabled";
      max_events_per_delivery: number; // int32
      summary_threshold: number; // int32
      min_interval_seconds: number; // int32
      pending_count: number; // int32
      dead_count: number; // int32
      succeeded_count: number; // int32
      oldest_pending_at: string | null; // date-time
      last_success_at: string | null; // date-time
      last_failure_at: string | null; // date-time
      last_failure: string | null;
      secrets: components["schemas"]["WebhookSecretInfoResponse"][];
      created_at: string; // date-time
      updated_at: string; // date-time
    };
    WebhookSecretInfoResponse: {
      id: string; // uuid
      created_at: string; // date-time
      expires_at: string | null; // date-time
    };
    WebhookSecretResponse: {
      webhook_id: string; // uuid
      secret_id: string; // uuid
      secret: string;
    };
    WorkplaceRefResponse: {
      site_id: string; // uuid
      building_id: string; // uuid
      building_code: string;
      building_name: string;
      floor_id: string; // uuid
      floor_label: string;
      level_order: number; // int32
      zone_key: string | null; // uuid
      zone_name: string | null;
      source: "seat" | "zone";
    };
    ZoneResponse: {
      zone_key: string; // uuid
      name: string;
      kind: "office" | "production_line" | "storage" | "common" | "restricted";
      polygon: number[][];
    };
  };
}

export interface paths {
  "/api/v1/admin/clients": {
    /** 自動スロットル・一時停止中のクライアントの一覧（人事管理者）（ListRestrictedClients） */
    get: {
      path: Record<string, never>;
      query: Record<string, never>;
      body: never;
      response: components["schemas"]["ClientStatusResponse"][];
    };
  };
  "/api/v1/admin/clients/{clientId}/release": {
    /** クライアントの一時停止・自動スロットルを解除する（人事管理者）（ReleaseClient） */
    post: {
      path: {
        clientId: string;
      };
      query: Record<string, never>;
      body: never;
      response: components["schemas"]["ClientStatusResponse"];
    };
  };
  "/api/v1/buildings/{id}": {
    /** 建物と階の一覧を参照する（GetBuilding） */
    get: {
      path: {
        id: string;
      };
      query: {
        as_of?: string;
      };
      body: never;
      response: components["schemas"]["BuildingDetailResponse"];
    };
  };
  "/api/v1/changes": {
    /** 変更フィード（GetChanges） */
    get: {
      path: Record<string, never>;
      query: {
        since?: string;
        limit?: string;
      };
      body: never;
      response: components["schemas"]["ChangeListResponse"];
    };
  };
  "/api/v1/corrections": {
    /** 修正依頼の一覧（閲覧者に見える範囲。起票の古い順）（ListCorrections） */
    get: {
      path: Record<string, never>;
      query: {
        status?: string;
        person_id?: string;
        cursor?: string;
        limit?: string;
      };
      body: never;
      response: components["schemas"]["CorrectionListResponse"];
    };
    /** 修正依頼を起票する（人・AI エージェント。jin-ji の公開データは変わらない）（CreateCorrection） */
    post: {
      path: Record<string, never>;
      query: Record<string, never>;
      body: components["schemas"]["CreateCorrectionRequest"];
      response: components["schemas"]["CreateCorrectionResponse"];
    };
  };
  "/api/v1/corrections/report.csv": {
    /** 修正依頼のレポート（CSV。UTF-8 BOM。修正担当者・人事管理者の人）（GetCorrectionReport） */
    get: {
      path: Record<string, never>;
      query: {
        status?: string;
      };
      body: never;
      response: void;
    };
  };
  "/api/v1/corrections/summary": {
    /** 修正依頼の件数（画面内の通知。修正担当者・人事管理者）（GetCorrectionSummary） */
    get: {
      path: Record<string, never>;
      query: Record<string, never>;
      body: never;
      response: components["schemas"]["CorrectionSummaryResponse"];
    };
  };
  "/api/v1/corrections/{correctionId}": {
    /** 修正依頼を参照する（GetCorrection） */
    get: {
      path: {
        correctionId: string;
      };
      query: Record<string, never>;
      body: never;
      response: components["schemas"]["CorrectionResponse"];
    };
  };
  "/api/v1/corrections/{correctionId}/close": {
    /** 修正依頼を対応済み・対応不要にする（修正担当者・人事管理者の人）（CloseCorrection） */
    post: {
      path: {
        correctionId: string;
      };
      query: Record<string, never>;
      body: components["schemas"]["CloseCorrectionRequest"];
      response: components["schemas"]["CorrectionResponse"];
    };
  };
  "/api/v1/field/grants": {
    /** 担当の指名の一覧（取り消し済みを含む。新しい順）（ListFieldEditorGrants） */
    get: {
      path: Record<string, never>;
      query: {
        org_unit_id?: string;
        person_id?: string;
      };
      body: never;
      response: components["schemas"]["FieldGrantResponse"][];
    };
    /** 組織の担当を指名する（組織長としての範囲を持つ人・組織管理者・人事管理者）（GrantFieldEditor） */
    post: {
      path: Record<string, never>;
      query: Record<string, never>;
      body: components["schemas"]["GrantFieldEditorRequest"];
      response: components["schemas"]["FieldGrantResponse"];
    };
  };
  "/api/v1/field/grants/{grantId}/revoke": {
    /** 担当の指名を取り消す（RevokeFieldEditor） */
    post: {
      path: {
        grantId: string;
      };
      query: Record<string, never>;
      body: never;
      response: components["schemas"]["FieldGrantResponse"];
    };
  };
  "/api/v1/field/modes": {
    /** 領域ごとの管理元（hr / field / both）（GetFieldModes） */
    get: {
      path: Record<string, never>;
      query: Record<string, never>;
      body: never;
      response: components["schemas"]["FieldModeResponse"][];
    };
  };
  "/api/v1/field/registrations": {
    /** 直接登録の一覧（自分の登録・同意した登録・自分の範囲の組織の登録。新しい順）（ListFieldRegistrations） */
    get: {
      path: Record<string, never>;
      query: {
        mine?: string;
        status?: string;
      };
      body: never;
      response: components["schemas"]["FieldRegistrationResponse"][];
    };
    /** 直接登録する（範囲の中ならすぐに反映。AI エージェントは下書き）（CreateFieldRegistration） */
    post: {
      path: Record<string, never>;
      query: Record<string, never>;
      body: components["schemas"]["CreateFieldRegistrationRequest"];
      response: components["schemas"]["FieldRegistrationResponse"];
    };
  };
  "/api/v1/field/registrations/{registrationId}": {
    /** 直接登録を参照する（変更を含む）（GetFieldRegistration） */
    get: {
      path: {
        registrationId: string;
      };
      query: Record<string, never>;
      body: never;
      response: components["schemas"]["FieldRegistrationResponse"];
    };
  };
  "/api/v1/field/registrations/{registrationId}/confirm": {
    /** AI エージェントの下書きを確定する（下書きの名義の人）（ConfirmFieldRegistration） */
    post: {
      path: {
        registrationId: string;
      };
      query: Record<string, never>;
      body: never;
      response: components["schemas"]["FieldRegistrationResponse"];
    };
  };
  "/api/v1/field/registrations/{registrationId}/consent": {
    /** 同意待ちの登録に同意して反映する（相手側の組織の範囲を持つ、登録者とは別の人）（ConsentFieldRegistration） */
    post: {
      path: {
        registrationId: string;
      };
      query: Record<string, never>;
      body: never;
      response: components["schemas"]["FieldRegistrationResponse"];
    };
  };
  "/api/v1/field/registrations/{registrationId}/discard": {
    /** 下書き・同意待ちの登録を破棄する（登録者か、組織管理者・人事管理者）（DiscardFieldRegistration） */
    post: {
      path: {
        registrationId: string;
      };
      query: Record<string, never>;
      body: never;
      response: components["schemas"]["FieldRegistrationResponse"];
    };
  };
  "/api/v1/field/registrations/{registrationId}/withdraw": {
    /** 反映済みの登録を有効日の前に取り消す（登録者か、登録の組織すべての範囲を持つ人）（WithdrawFieldRegistration） */
    post: {
      path: {
        registrationId: string;
      };
      query: Record<string, never>;
      body: never;
      response: components["schemas"]["FieldRegistrationResponse"];
    };
  };
  "/api/v1/field/scope": {
    /** 閲覧者の直接登録の範囲（今日。組織の ID の一覧）（GetFieldScope） */
    get: {
      path: Record<string, never>;
      query: Record<string, never>;
      body: never;
      response: components["schemas"]["FieldScopeResponse"];
    };
  };
  "/api/v1/field/settings": {
    /** 管理元の起案の一覧（新しい順）（ListFieldSettings） */
    get: {
      path: Record<string, never>;
      query: Record<string, never>;
      body: never;
      response: components["schemas"]["FieldSettingResponse"][];
    };
    /** 管理元を起案する（人事管理者・組織管理者の人）（ProposeFieldSetting） */
    post: {
      path: Record<string, never>;
      query: Record<string, never>;
      body: components["schemas"]["ProposeFieldSettingRequest"];
      response: components["schemas"]["FieldSettingResponse"];
    };
  };
  "/api/v1/field/settings/{settingId}/approve": {
    /** 管理元の起案を承認する（起案者とは別の人事管理者・組織管理者の人）（ApproveFieldSetting） */
    post: {
      path: {
        settingId: string;
      };
      query: Record<string, never>;
      body: never;
      response: components["schemas"]["FieldSettingResponse"];
    };
  };
  "/api/v1/field/settings/{settingId}/reject": {
    /** 管理元の起案を却下する（起案者とは別の人事管理者・組織管理者の人）（RejectFieldSetting） */
    post: {
      path: {
        settingId: string;
      };
      query: Record<string, never>;
      body: never;
      response: components["schemas"]["FieldSettingResponse"];
    };
  };
  "/api/v1/floors/{id}/layout": {
    /** その日の配置図（外形・固定の部分・ゾーン・席の置き場所と状態）（GetFloorLayout） */
    get: {
      path: {
        id: string;
      };
      query: {
        as_of?: string;
      };
      body: never;
      response: components["schemas"]["FloorLayoutResponse"];
    };
  };
  "/api/v1/floors/{id}/occupants": {
    /** 階の在籍者と席（所属上の席だけ。当日の所在は含めない）（ListFloorOccupants） */
    get: {
      path: {
        id: string;
      };
      query: {
        zone?: string;
        limit?: string;
        cursor?: string;
        as_of?: string;
      };
      body: never;
      response: components["schemas"]["FloorOccupantListResponse"];
    };
  };
  "/api/v1/floors/{id}/presence": {
    /** 今日の階の所在（外部の提供を注入したときだけ）（GetFloorPresence） */
    get: {
      path: {
        id: string;
      };
      query: Record<string, never>;
      body: never;
      response: components["schemas"]["FloorPresenceResponse"];
    };
  };
  "/api/v1/groups": {
    /** グループの一覧（今日有効な、閲覧者に見えるもの。名前の順）（ListGroups） */
    get: {
      path: Record<string, never>;
      query: {
        kind?: string;
        person_id?: string;
        owner_org_unit_id?: string;
        managed?: string;
      };
      body: never;
      response: components["schemas"]["GroupResponse"][];
    };
    /** グループを作る（人。作成者が最初のオーナー）（CreateGroup） */
    post: {
      path: Record<string, never>;
      query: Record<string, never>;
      body: components["schemas"]["CreateGroupRequest"];
      response: components["schemas"]["GroupDetailResponse"];
    };
  };
  "/api/v1/groups/{groupId}": {
    /** グループと今日のメンバーを参照する（GetGroup） */
    get: {
      path: {
        groupId: string;
      };
      query: Record<string, never>;
      body: never;
      response: components["schemas"]["GroupDetailResponse"];
    };
  };
  "/api/v1/groups/{groupId}/close": {
    /** その日（既定は今日）でグループを終える（CloseGroup） */
    post: {
      path: {
        groupId: string;
      };
      query: Record<string, never>;
      body: components["schemas"]["CloseGroupRequest"] | null;
      response: components["schemas"]["GroupDetailResponse"];
    };
  };
  "/api/v1/groups/{groupId}/members": {
    /** メンバーを加える（AddGroupMember） */
    post: {
      path: {
        groupId: string;
      };
      query: Record<string, never>;
      body: components["schemas"]["AddGroupMemberRequest"];
      response: components["schemas"]["GroupDetailResponse"];
    };
  };
  "/api/v1/groups/{groupId}/members/{personId}/remove": {
    /** 今日でメンバーから外す（本人は自分を外せる。管理対象のグループを除く）（RemoveGroupMember） */
    post: {
      path: {
        groupId: string;
        personId: string;
      };
      query: Record<string, never>;
      body: never;
      response: components["schemas"]["GroupDetailResponse"];
    };
  };
  "/api/v1/groups/{groupId}/members/{personId}/role": {
    /** 今日からメンバーの役割を変える（ChangeGroupMemberRole） */
    post: {
      path: {
        groupId: string;
        personId: string;
      };
      query: Record<string, never>;
      body: components["schemas"]["ChangeGroupRoleRequest"];
      response: components["schemas"]["GroupDetailResponse"];
    };
  };
  "/api/v1/groups/{groupId}/settings": {
    /** 公開範囲（all / members）・管理対象を変える（SetGroupSettings） */
    post: {
      path: {
        groupId: string;
      };
      query: Record<string, never>;
      body: components["schemas"]["GroupSettingsRequest"];
      response: components["schemas"]["GroupDetailResponse"];
    };
  };
  "/api/v1/groups/{groupId}/update": {
    /** 今日から名称・種別・所管組織を変える（UpdateGroup） */
    post: {
      path: {
        groupId: string;
      };
      query: Record<string, never>;
      body: components["schemas"]["UpdateGroupRequest"];
      response: components["schemas"]["GroupDetailResponse"];
    };
  };
  "/api/v1/ingest/proposals/{proposalId}": {
    /** 変更提案の根拠（候補・LLM による推定）を参照する（人事管理者）（GetProposal） */
    get: {
      path: {
        proposalId: string;
      };
      query: Record<string, never>;
      body: never;
      response: components["schemas"]["ProposalResponse"];
    };
  };
  "/api/v1/ingest/proposals/{proposalId}/resolve": {
    /** 要確認の提案を解決する（人事管理者。取込の実行者は不可）（ResolveProposal） */
    post: {
      path: {
        proposalId: string;
      };
      query: Record<string, never>;
      body: components["schemas"]["ResolveProposalRequest"];
      response: components["schemas"]["ResolveProposalResponse"];
    };
  };
  "/api/v1/meta": {
    /** メタ情報（業務日付・段の定義・時点の目盛り）（GetMeta） */
    get: {
      path: Record<string, never>;
      query: Record<string, never>;
      body: never;
      response: components["schemas"]["MetaResponse"];
    };
  };
  "/api/v1/orgs": {
    /** 組織を検索する（SearchOrgs） */
    get: {
      path: Record<string, never>;
      query: {
        query?: string;
        as_of?: string;
        limit?: string;
      };
      body: never;
      response: components["schemas"]["OrgSearchResponse"];
    };
  };
  "/api/v1/orgs/stats": {
    /** 組織ごとの人数と、兼務・出向の組織間の集計（GetOrgStats） */
    get: {
      path: Record<string, never>;
      query: {
        as_of?: string;
      };
      body: never;
      response: components["schemas"]["OrgStatsResponse"];
    };
  };
  "/api/v1/orgs/tree": {
    /** 組織ツリーを参照する（GetOrgTree） */
    get: {
      path: Record<string, never>;
      query: {
        as_of?: string;
      };
      body: never;
      response: components["schemas"]["OrgTreeResponse"];
    };
  };
  "/api/v1/orgs/{id}": {
    /** 組織を参照する（GetOrg） */
    get: {
      path: {
        id: string;
      };
      query: {
        as_of?: string;
      };
      body: never;
      response: components["schemas"]["OrgUnitResponse"];
    };
  };
  "/api/v1/orgs/{id}/members": {
    /** 組織の所属者を参照する（ListOrgMembers） */
    get: {
      path: {
        id: string;
      };
      query: {
        include_descendants?: string;
        as_of?: string;
        cursor?: string;
        limit?: string;
      };
      body: never;
      response: components["schemas"]["PersonListResponse"];
    };
  };
  "/api/v1/people": {
    /** 人を検索する・ID で一括取得する（ListPeople） */
    get: {
      path: Record<string, never>;
      query: {
        ids?: string;
        query?: string;
        org?: string;
        include_descendants?: string;
        legal_entity?: string[];
        level?: string[];
        assignment_kind?: string[];
        position?: string[];
        employment_type?: string[];
        job_type?: string[];
        site?: string[];
        site_type?: string[];
        prefecture?: string[];
        remote_work?: string;
        hired_from?: string;
        hired_to?: string;
        employment_status?: string[];
        account_status?: string[];
        sort?: string;
        facets?: string;
        as_of?: string;
        cursor?: string;
        limit?: string;
      };
      body: never;
      response: components["schemas"]["PersonSearchResponse"];
    };
  };
  "/api/v1/people/{id}": {
    /** 人を参照する（GetPerson） */
    get: {
      path: {
        id: string;
      };
      query: {
        as_of?: string;
      };
      body: never;
      response: components["schemas"]["PersonResponse"];
    };
  };
  "/api/v1/people/{id}/approval-route": {
    /** 人の承認ルート（申請を誰が承認するか）を参照する（GetApprovalRoute） */
    get: {
      path: {
        id: string;
      };
      query: {
        org_unit_id?: string;
        vacancy?: string;
        level?: string[];
        position?: string[];
        as_of?: string;
      };
      body: never;
      response: components["schemas"]["ApprovalRouteResponse"];
    };
  };
  "/api/v1/people/{id}/manager": {
    /** 人の直属の上長を参照する（GetPersonManager） */
    get: {
      path: {
        id: string;
      };
      query: {
        as_of?: string;
      };
      body: never;
      response: components["schemas"]["ManagerResponse"];
    };
  };
  "/api/v1/seats": {
    /** 席の検索（空席など。人は返さない）（SearchSeats） */
    get: {
      path: Record<string, never>;
      query: {
        floor?: string[];
        building?: string;
        site?: string;
        zone?: string;
        kind?: string[];
        feature?: string[];
        vacant?: string;
        limit?: string;
        as_of?: string;
      };
      body: never;
      response: components["schemas"]["SeatSearchResponse"];
    };
  };
  "/api/v1/sites": {
    /** 拠点の一覧と、拠点ごとの人数（ListSites） */
    get: {
      path: Record<string, never>;
      query: {
        query?: string;
        legal_entity?: string[];
        site_type?: string[];
        prefecture?: string[];
        as_of?: string;
      };
      body: never;
      response: components["schemas"]["SiteListResponse"];
    };
  };
  "/api/v1/sites/{id}": {
    /** 拠点を参照する（GetSite） */
    get: {
      path: {
        id: string;
      };
      query: {
        as_of?: string;
      };
      body: never;
      response: components["schemas"]["SiteResponse"];
    };
  };
  "/api/v1/sites/{id}/buildings": {
    /** 拠点の建物と階の一覧（階ごとの席数・使用中・空席・使えない席）（ListSiteBuildings） */
    get: {
      path: {
        id: string;
      };
      query: {
        as_of?: string;
      };
      body: never;
      response: components["schemas"]["BuildingListResponse"];
    };
  };
  "/api/v1/sync/plans": {
    /** 同期計画の一覧（人事管理者）（ListSyncPlans） */
    get: {
      path: Record<string, never>;
      query: {
        limit?: string;
      };
      body: never;
      response: components["schemas"]["SyncPlanResponse"][];
    };
  };
  "/api/v1/sync/plans/{planId}": {
    /** 同期計画の詳細（項目・根拠・実行結果の照合。人事管理者）（GetSyncPlan） */
    get: {
      path: {
        planId: string;
      };
      query: Record<string, never>;
      body: never;
      response: components["schemas"]["SyncPlanDetailResponse"];
    };
  };
  "/api/v1/sync/plans/{planId}/approve": {
    /** 同期計画を承認する（人事管理者。起案者は不可）（ApproveSyncPlan） */
    post: {
      path: {
        planId: string;
      };
      query: Record<string, never>;
      body: never;
      response: components["schemas"]["SyncPlanResponse"];
    };
  };
  "/api/v1/sync/plans/{planId}/reject": {
    /** 同期計画を却下する（人事管理者。起案者は不可）（RejectSyncPlan） */
    post: {
      path: {
        planId: string;
      };
      query: Record<string, never>;
      body: never;
      response: components["schemas"]["SyncPlanResponse"];
    };
  };
  "/api/v1/sync/plans/{planId}/script": {
    /** 承認済みの同期計画のスクリプト一式（ZIP: .ps1・.data.json・.report.md・.report.csv。人事管理者）（GetSyncScript） */
    get: {
      path: {
        planId: string;
      };
      query: Record<string, never>;
      body: never;
      response: void;
    };
  };
  "/auth/account/passkeys": {
    /** 登録したパスキーの一覧（ローカルアカウントでログイン中の本人） */
    get: {
      path: Record<string, never>;
      query: Record<string, never>;
      body: never;
      response: components["schemas"]["PasskeyResponse"][];
    };
    /** パスキーを登録する */
    post: {
      path: Record<string, never>;
      query: Record<string, never>;
      body: components["schemas"]["PasskeyCredentialRequest"];
      response: void;
    };
  };
  "/auth/account/passkeys/options": {
    /** パスキーの登録の options（navigator.credentials.create に渡す JSON） */
    post: {
      path: Record<string, never>;
      query: Record<string, never>;
      body: never;
      response: components["schemas"]["JsonElement"];
    };
  };
  "/auth/account/passkeys/{credentialId}/delete": {
    /** パスキーを削除する（最後の 1 つでログインの手段か管理系のロールの多要素が無くなる場合は 409） */
    post: {
      path: {
        credentialId: string;
      };
      query: Record<string, never>;
      body: never;
      response: void;
    };
  };
  "/auth/account/two-factor/enable": {
    /** TOTP を有効にしてリカバリコードを受け取る */
    post: {
      path: Record<string, never>;
      query: Record<string, never>;
      body: components["schemas"]["EnableTwoFactorRequest"];
      response: components["schemas"]["RecoveryCodesResponse"];
    };
  };
  "/auth/account/two-factor/setup": {
    /** TOTP の共有鍵を作り直す（有効にするのは enable で確かめてから） */
    post: {
      path: Record<string, never>;
      query: Record<string, never>;
      body: never;
      response: components["schemas"]["TwoFactorSetupResponse"];
    };
  };
  "/auth/admin/local-accounts": {
    /** ローカルアカウント（人のアカウント・緊急用アカウント）の一覧。query はユーザー名の部分一致 */
    get: {
      path: Record<string, never>;
      query: {
        query?: string;
        limit?: number;
      };
      body: never;
      response: components["schemas"]["LocalAccountResponse"][];
    };
  };
  "/auth/admin/local-accounts/{accountId}/disable": {
    /** ローカルアカウントを無効にする（セッションを終え、人のアカウントならその人の委任をすべて失効する） */
    post: {
      path: {
        accountId: string;
      };
      query: Record<string, never>;
      body: never;
      response: components["schemas"]["DisableAccountResponse"];
    };
  };
  "/auth/admin/local-accounts/{accountId}/enable": {
    /** 無効にしたローカルアカウントを有効に戻す（終えたセッション・失効した委任は戻らない） */
    post: {
      path: {
        accountId: string;
      };
      query: Record<string, never>;
      body: never;
      response: void;
    };
  };
  "/auth/admin/local-accounts/{accountId}/reset-links": {
    /** パスワードの設定・パスキーの登録の 1 回限りのリンクを発行する */
    post: {
      path: {
        accountId: string;
      };
      query: Record<string, never>;
      body: components["schemas"]["IssueResetLinkRequest"];
      response: components["schemas"]["ResetLinkResponse"];
    };
  };
  "/auth/admin/local-accounts/{accountId}/unlock": {
    /** ローカルアカウントのロックアウトを解除する */
    post: {
      path: {
        accountId: string;
      };
      query: Record<string, never>;
      body: never;
      response: void;
    };
  };
  "/auth/admin/login-bindings": {
    /** 外部 IdP の主体を人に紐付ける（未紐付けのログインから、またはログイン方式と主体の識別子で） */
    post: {
      path: Record<string, never>;
      query: Record<string, never>;
      body: components["schemas"]["CreateLoginBindingRequest"];
      response: components["schemas"]["LoginBindingResponse"];
    };
  };
  "/auth/admin/login-bindings/{bindingId}/revoke": {
    /** 紐付けを取り消す（その紐付けのセッションも終える） */
    post: {
      path: {
        bindingId: string;
      };
      query: Record<string, never>;
      body: never;
      response: void;
    };
  };
  "/auth/admin/oauth/cimd-domains": {
    /** CIMD の取得を許すドメイン（設定の既定と、管理者が足したもの） */
    get: {
      path: Record<string, never>;
      query: Record<string, never>;
      body: never;
      response: components["schemas"]["CimdDomainsResponse"];
    };
    /** CIMD の取得を許すドメインを足す */
    post: {
      path: Record<string, never>;
      query: Record<string, never>;
      body: components["schemas"]["AddCimdDomainRequest"];
      response: void;
    };
  };
  "/auth/admin/oauth/cimd-domains/{domain}/remove": {
    /** CIMD の取得を許すドメインを外す（そのドメインから登録済みのクライアントは停止しない） */
    post: {
      path: {
        domain: string;
      };
      query: Record<string, never>;
      body: never;
      response: void;
    };
  };
  "/auth/admin/oauth/clients": {
    /** トークン発行のクライアントの一覧（status で絞れる。承認待ちは pending_approval） */
    get: {
      path: Record<string, never>;
      query: {
        status?: string;
      };
      body: never;
      response: components["schemas"]["OAuthClientResponse"][];
    };
    /** クライアントを事前登録する */
    post: {
      path: Record<string, never>;
      query: Record<string, never>;
      body: components["schemas"]["RegisterClientRequest"];
      response: components["schemas"]["OAuthClientResponse"];
    };
  };
  "/auth/admin/oauth/clients/{clientRef}/disclosure-leads": {
    /** 社内システム（client credentials）の先行提供の日数（有効日の何日前から将来の予定を見せるか。0〜90）を起案する。別の人事管理者の承認で有効になる */
    post: {
      path: {
        clientRef: string;
      };
      query: Record<string, never>;
      body: components["schemas"]["ProposeDisclosureLeadRequest"];
      response: components["schemas"]["DisclosureLeadResponse"];
    };
  };
  "/auth/admin/oauth/clients/{clientRef}/keys": {
    /** private_key_jwt の公開鍵を登録する */
    post: {
      path: {
        clientRef: string;
      };
      query: Record<string, never>;
      body: components["schemas"]["AddClientKeyRequest"];
      response: void;
    };
  };
  "/auth/admin/oauth/clients/{clientRef}/keys/{keyId}/revoke": {
    /** 公開鍵を取り消す */
    post: {
      path: {
        clientRef: string;
        keyId: string;
      };
      query: Record<string, never>;
      body: never;
      response: void;
    };
  };
  "/auth/admin/oauth/clients/{clientRef}/revoke-tokens": {
    /** クライアントのトークンの系列を失効する（person_id を指定すればその人の分だけ） */
    post: {
      path: {
        clientRef: string;
      };
      query: Record<string, never>;
      body: components["schemas"]["RevokeClientTokensRequest"];
      response: components["schemas"]["RevokedTokensResponse"];
    };
  };
  "/auth/admin/oauth/clients/{clientRef}/secrets": {
    /** 移行用の client_secret を発行する（secret はこの応答でだけ返る） */
    post: {
      path: {
        clientRef: string;
      };
      query: Record<string, never>;
      body: components["schemas"]["IssueClientSecretRequest"];
      response: components["schemas"]["ClientSecretResponse"];
    };
  };
  "/auth/admin/oauth/clients/{clientRef}/secrets/revoke": {
    /** client_secret をすべて取り消す */
    post: {
      path: {
        clientRef: string;
      };
      query: Record<string, never>;
      body: never;
      response: void;
    };
  };
  "/auth/admin/oauth/clients/{clientRef}/update": {
    /** クライアントの登録を変える（指定した項目だけ） */
    post: {
      path: {
        clientRef: string;
      };
      query: Record<string, never>;
      body: components["schemas"]["UpdateClientRequest"];
      response: components["schemas"]["OAuthClientResponse"];
    };
  };
  "/auth/admin/oauth/clients/{clientRef}/webhook": {
    /** 社内システム（client credentials で REST API を読めるクライアント）に Webhook を登録する（クライアントごとに 1 つ）。署名の鍵はこの応答でだけ返る */
    post: {
      path: {
        clientRef: string;
      };
      query: Record<string, never>;
      body: components["schemas"]["RegisterWebhookRequest"];
      response: components["schemas"]["WebhookSecretResponse"];
    };
  };
  "/auth/admin/oauth/clients/{clientRef}/{action}": {
    /** クライアントを停止・再開する、承認待ちの CIMD のクライアントを承認・却下する */
    post: {
      path: {
        clientRef: string;
        action: string;
      };
      query: Record<string, never>;
      body: never;
      response: components["schemas"]["OAuthClientResponse"];
    };
  };
  "/auth/admin/oauth/disclosure-leads": {
    /** 先行提供の日数の起案の一覧（新しい順。client でクライアントを絞れる） */
    get: {
      path: Record<string, never>;
      query: {
        client?: string;
      };
      body: never;
      response: components["schemas"]["DisclosureLeadResponse"][];
    };
  };
  "/auth/admin/oauth/disclosure-leads/{leadId}/{action}": {
    /** 先行提供の日数の起案を承認・却下する（起案者本人は不可） */
    post: {
      path: {
        leadId: string;
        action: string;
      };
      query: Record<string, never>;
      body: never;
      response: components["schemas"]["DisclosureLeadResponse"];
    };
  };
  "/auth/admin/oauth/webhooks": {
    /** 社内システムへの Webhook の一覧（受け手のクライアント・送信待ち・dead・成功の件数・最終の成功と失敗・署名の鍵の期限） */
    get: {
      path: Record<string, never>;
      query: Record<string, never>;
      body: never;
      response: components["schemas"]["WebhookResponse"][];
    };
  };
  "/auth/admin/oauth/webhooks/{webhookId}/deliveries": {
    /** Webhook の送信の一覧（新しい順。status で絞れる。本文は種別と ID だけ） */
    get: {
      path: {
        webhookId: string;
      };
      query: {
        status?: string;
        limit?: number;
      };
      body: never;
      response: components["schemas"]["WebhookDeliveryResponse"][];
    };
  };
  "/auth/admin/oauth/webhooks/{webhookId}/ping": {
    /** 送信の確かめ（ping。人事データを持たない本文）を積む。受け手の署名の検証の確かめに使う */
    post: {
      path: {
        webhookId: string;
      };
      query: Record<string, never>;
      body: never;
      response: components["schemas"]["WebhookPingResponse"];
    };
  };
  "/auth/admin/oauth/webhooks/{webhookId}/redeliver": {
    /** dead の送信を再送する（delivery_id を指定しなければ、その Webhook の dead をすべて。同じイベント ID・本文で送る） */
    post: {
      path: {
        webhookId: string;
      };
      query: Record<string, never>;
      body: components["schemas"]["RedeliverWebhookRequest"];
      response: components["schemas"]["WebhookRedeliveredResponse"];
    };
  };
  "/auth/admin/oauth/webhooks/{webhookId}/rotate-secret": {
    /** 署名の鍵を入れ替える。旧鍵でも overlap_hours（既定 24、0〜168）の間は署名を続ける。新しい鍵はこの応答でだけ返る */
    post: {
      path: {
        webhookId: string;
      };
      query: Record<string, never>;
      body: components["schemas"]["RotateWebhookSecretRequest"];
      response: components["schemas"]["WebhookSecretResponse"];
    };
  };
  "/auth/admin/oauth/webhooks/{webhookId}/update": {
    /** Webhook の送信先・状態（active / disabled）・1 回に送る件数・要約に切り替える件数・送信の間隔を変える（指定した項目だけ） */
    post: {
      path: {
        webhookId: string;
      };
      query: Record<string, never>;
      body: components["schemas"]["UpdateWebhookRequest"];
      response: components["schemas"]["WebhookResponse"];
    };
  };
  "/auth/admin/unbound-logins": {
    /** 紐付けできずに拒否したログインの一覧（人事管理者・緊急用アカウント） */
    get: {
      path: Record<string, never>;
      query: {
        include_resolved?: boolean;
        limit?: number;
      };
      body: never;
      response: components["schemas"]["UnboundLoginResponse"][];
    };
  };
  "/auth/csrf": {
    /** CSRF の要求トークン（状態を変える要求の X-JinJi-CSRF ヘッダーに付ける） */
    get: {
      path: Record<string, never>;
      query: Record<string, never>;
      body: never;
      response: components["schemas"]["CsrfResponse"];
    };
  };
  "/auth/login/passkey": {
    /** パスキーでログイン */
    post: {
      path: Record<string, never>;
      query: Record<string, never>;
      body: components["schemas"]["PasskeyCredentialRequest"];
      response: components["schemas"]["LoginResponse"];
    };
  };
  "/auth/login/passkey/options": {
    /** パスキーでのログインの options（navigator.credentials.get に渡す JSON） */
    post: {
      path: Record<string, never>;
      query: Record<string, never>;
      body: never;
      response: components["schemas"]["JsonElement"];
    };
  };
  "/auth/login/password": {
    /** パスワードでログイン（TOTP が有効なら two_factor_required） */
    post: {
      path: Record<string, never>;
      query: Record<string, never>;
      body: components["schemas"]["PasswordLoginRequest"];
      response: components["schemas"]["LoginResponse"];
    };
  };
  "/auth/login/two-factor": {
    /** TOTP かリカバリコードでログインを完了する */
    post: {
      path: Record<string, never>;
      query: Record<string, never>;
      body: components["schemas"]["TwoFactorLoginRequest"];
      response: components["schemas"]["LoginResponse"];
    };
  };
  "/auth/logout": {
    /** ログアウト（セッションを終える） */
    post: {
      path: Record<string, never>;
      query: Record<string, never>;
      body: never;
      response: void;
    };
  };
  "/auth/me": {
    /** ログイン中のセッション（UI の Cookie のセッションだけ） */
    get: {
      path: Record<string, never>;
      query: Record<string, never>;
      body: never;
      response: components["schemas"]["SessionResponse"];
    };
  };
  "/auth/providers": {
    /** 使えるログイン方式 */
    get: {
      path: Record<string, never>;
      query: Record<string, never>;
      body: never;
      response: components["schemas"]["LoginMethodsResponse"];
    };
  };
  "/auth/reset/passkey": {
    /** リンクでパスキーを登録する */
    post: {
      path: Record<string, never>;
      query: Record<string, never>;
      body: components["schemas"]["ResetPasskeyRequest"];
      response: void;
    };
  };
  "/auth/reset/passkey/options": {
    /** リンクでのパスキーの登録の options */
    post: {
      path: Record<string, never>;
      query: Record<string, never>;
      body: components["schemas"]["ResetPasskeyOptionsRequest"];
      response: components["schemas"]["JsonElement"];
    };
  };
  "/auth/reset/password": {
    /** リンクでパスワードを設定する */
    post: {
      path: Record<string, never>;
      query: Record<string, never>;
      body: components["schemas"]["ResetPasswordRequest"];
      response: void;
    };
  };
  "/oauth/delegations": {
    /** 本人が AI エージェント（MCP クライアントなど）に許可している委任の一覧（UI でログインした本人だけ） */
    get: {
      path: Record<string, never>;
      query: Record<string, never>;
      body: never;
      response: components["schemas"]["DelegationResponse"][];
    };
  };
  "/oauth/delegations/{familyId}/revoke": {
    /** 本人の委任を取り消す（そのクライアントのトークンは次の要求から使えない） */
    post: {
      path: {
        familyId: string;
      };
      query: Record<string, never>;
      body: never;
      response: void;
    };
  };
  "/oauth/requests/{requestId}": {
    /** 同意画面に出す認可の要求（UI でログインした人だけ） */
    get: {
      path: {
        requestId: string;
      };
      query: Record<string, never>;
      body: never;
      response: components["schemas"]["ConsentResponse"];
    };
  };
  "/oauth/requests/{requestId}/decision": {
    /** 認可の要求に同意・拒否する（redirect_to へ遷移する） */
    post: {
      path: {
        requestId: string;
      };
      query: Record<string, never>;
      body: components["schemas"]["ConsentDecisionRequest"];
      response: components["schemas"]["ConsentDecisionResponse"];
    };
  };
}

export type Schemas = components["schemas"];
