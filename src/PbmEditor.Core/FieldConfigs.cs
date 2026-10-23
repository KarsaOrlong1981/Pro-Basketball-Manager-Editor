namespace PbmEditor.Core;

/// <summary>Labels, groups and editors for known columns. Unknown or missing columns are tolerated.</summary>
public static class FieldConfigs
{
    public static readonly string[] AttributeColumns =
    {
        "attribut_3_points", "attribut_2_points", "attribut_lancer_franc", "attribut_creation_shoot", "attribut_tir_interieur",
        "attribut_dribble", "attribut_passe", "attribut_penetration",
        "attribut_defense_ext", "attribut_defense_int", "attribut_interception", "attribut_rebond_off", "attribut_rebond_def", "attribut_contre",
        "attribut_vitesse", "attribut_endurance", "attribut_detente", "attribut_puissance", "attribut_acc", "attribut_explosivite",
        "attribut_collectif", "attribut_sang_froid", "attribut_placement", "attribut_influence", "attribut_regularite", "attribut_experience",
    };

    public static FieldSet Team() => new FieldSet()
        .Add("id_equipe", "Team ID", "Identity")
        .Add("nom_equipe", "Name", "Identity")
        .Add("nom_court_equipe", "Short name", "Identity")
        .Add("abrev_nom", "Abbreviation (name)", "Identity")
        .Add("abrev_logo", "Abbreviation (logo)", "Identity")
        .Add("fake_name", "Fake name", "Identity")
        .Add("creation", "Founded (year)", "Identity")
        .Add("president", "President", "Identity")
        .Add("championnat_id", "League", "Identity", choices: GameService.LeagueChoices)
        .Add("pays_id", "Country", "Identity", choices: GameService.CountryChoices)
        .Add("id_ville", "City", "Identity", choices: GameService.CityChoices)
        .Add("budget", "Budget", "Finance")
        .Add("real_budget", "Real budget", "Finance")
        .Add("masse_salariale", "Payroll", "Finance")
        .Add("masse_salariale_staff", "Staff payroll", "Finance")
        .Add("owns_arena", "Owns arena", "Finance", FieldKind.Bool)
        .Add("yearly_rent_arena", "Yearly arena rent", "Finance")
        .Add("budget_coefficient_compared_league", "Budget coefficient vs league", "Finance", FieldKind.Real)
        .Add("supporter", "Supporters", "Fans & prestige")
        .Add("renomee", "Reputation (national)", "Fans & prestige", FieldKind.Slider, 0, 100)
        .Add("renomee_mondiale", "Reputation (world)", "Fans & prestige", FieldKind.Slider, 0, 100)
        .Add("fans_loyalty", "Fan loyalty", "Fans & prestige", FieldKind.Slider, 0, 100)
        .Add("fans_passion", "Fan passion", "Fans & prestige", FieldKind.Slider, 0, 100)
        .Add("fans_patience", "Fan patience", "Fans & prestige", FieldKind.Slider, 0, 100)
        .Add("club_patience", "Club patience", "Fans & prestige", FieldKind.Slider, 0, 100)
        .Add("capacite_salle", "Arena capacity", "Arena")
        .Add("id_salle", "Arena ID", "Arena")
        .Add("couleur_home", "Home colour", "Colours", FieldKind.Color)
        .Add("couleur_away", "Away colour", "Colours", FieldKind.Color)
        .Add("color_3rd", "Third colour", "Colours", FieldKind.Color)
        .Add("ennemie_1", "Rival 1", "Rivals", choices: GameService.TeamChoices)
        .Add("ennemie_2", "Rival 2", "Rivals", choices: GameService.TeamChoices)
        .Add("equipe_associee", "Associated team", "Rivals", choices: GameService.TeamChoices)
        .Add("isActive", "Active", "Other", FieldKind.Bool);

    public static FieldSet Arena() => new FieldSet()
        .Add("id_facility", "Facility ID", "Arena")
        .Add("name_arena", "Arena name", "Arena")
        .Add("capacity", "Capacity", "Arena")
        .Add("id_city", "City", "Arena", choices: GameService.CityChoices)
        .Add("type_arena", "Arena type", "Arena");

    public static FieldSet Player() => new FieldSet()
        .Add("joueur_id", "Player ID", "Identity")
        .Add("joueur_prenom", "First name", "Identity")
        .Add("joueur_nom", "Last name", "Identity")
        .Add("joueur_middle", "Middle name", "Identity")
        .Add("joueur_suffixe", "Suffix", "Identity")
        .Add("joueur_surnom", "Nickname", "Identity")
        .Add("team_id", "Team", "Identity", choices: GameService.TeamChoicesWithFreeAgent)
        .Add("numero_actuel", "Jersey number (255 = none)", "Identity")
        .Add("nationalite", "Nationality", "Identity", choices: GameService.CountryChoices)
        .Add("nationalite_bis", "2nd nationality", "Identity", choices: GameService.CountryChoicesWithNone)
        .Add("jour_naissance", "Birth day", "Body & age")
        .Add("mois_naissance", "Birth month", "Body & age")
        .Add("annee_naissance", "Birth year", "Body & age")
        .Add("taille", "Height (cm)", "Body & age")
        .Add("poids", "Weight (kg)", "Body & age")
        .Add("main_droite", "Right hand", "Body & age", FieldKind.Slider, 0, 20, 0.5)
        .Add("main_gauche", "Left hand", "Body & age", FieldKind.Slider, 0, 20, 0.5)
        .Add("couleur", "Skin colour (0-60)", "In-game look", FieldKind.Slider, 0, 60)
        .Add("type_coiffure", "Hairstyle", "In-game look", choices: GameService.HairstyleChoices)
        .Add("couleur_cheveux", "Hair colour (0-4)", "In-game look", FieldKind.Slider, 0, 4)
        .Add("type_barbe", "Beard (0 = none)", "In-game look")
        .Add("moyenne", "Overall rating", "Rating", FieldKind.Real)
        .Add("joueur_valeur", "Market value", "Rating")
        .Add("aptitude_meneur", "Aptitude: PG", "Positions", FieldKind.Slider, 0, 100)
        .Add("aptitude_arriere", "Aptitude: SG", "Positions", FieldKind.Slider, 0, 100)
        .Add("aptitude_ailier", "Aptitude: Wing", "Positions", FieldKind.Slider, 0, 100)
        .Add("aptitude_interieur", "Aptitude: PF", "Positions", FieldKind.Slider, 0, 100)
        .Add("aptitude_pivot", "Aptitude: C", "Positions", FieldKind.Slider, 0, 100);

    public static FieldSet Attributes()
    {
        var s = new FieldSet();
        void A(string col, string label, string group) => s.Add(col, label, group, FieldKind.Slider, 0, 20, 0.5);
        A("attribut_3_points", "3-point shot", "Shooting");
        A("attribut_2_points", "Mid-range shot", "Shooting");
        A("attribut_lancer_franc", "Free throw", "Shooting");
        A("attribut_creation_shoot", "Shot creation", "Shooting");
        A("attribut_tir_interieur", "Inside shot", "Shooting");
        A("attribut_dribble", "Dribbling", "Ball skills");
        A("attribut_passe", "Passing", "Ball skills");
        A("attribut_penetration", "Drive", "Ball skills");
        A("attribut_defense_ext", "Perimeter defense", "Defense");
        A("attribut_defense_int", "Interior defense", "Defense");
        A("attribut_interception", "Steals", "Defense");
        A("attribut_rebond_off", "Offensive rebound", "Defense");
        A("attribut_rebond_def", "Defensive rebound", "Defense");
        A("attribut_contre", "Blocks", "Defense");
        A("attribut_vitesse", "Speed", "Athletics");
        A("attribut_endurance", "Stamina", "Athletics");
        A("attribut_detente", "Vertical leap", "Athletics");
        A("attribut_puissance", "Strength", "Athletics");
        A("attribut_acc", "Acceleration", "Athletics");
        A("attribut_explosivite", "Explosiveness", "Athletics");
        A("attribut_collectif", "Team play", "Mental");
        A("attribut_sang_froid", "Composure", "Mental");
        A("attribut_placement", "Positioning", "Mental");
        A("attribut_influence", "Influence", "Mental");
        A("attribut_regularite", "Consistency", "Mental");
        A("attribut_experience", "Experience", "Mental");
        s.Add("moyenne", "Overall (attributes table)", "Rating", FieldKind.Real);
        s.Add("cache_potentiel", "Potential", "Rating", FieldKind.Real);
        s.Add("cache_motivation", "Motivation", "Rating", FieldKind.Real);
        s.Add("cache_frequence_blessure", "Injury frequency", "Rating", FieldKind.Real);
        return s;
    }

    public static FieldSet Contract() => new FieldSet()
        .Add("id", "Contract ID", "Contract")
        .Add("salaire", "Monthly salary", "Contract")
        .Add("prime", "Bonus", "Contract")
        .Add("victoire_championnat", "League title bonus", "Contract")
        .Add("victoire_coupe", "Cup win bonus", "Contract")
        .Add("debut_jour", "Start day", "Term")
        .Add("debut_mois", "Start month", "Term")
        .Add("debut_annee", "Start year", "Term")
        .Add("fin_jour", "End day", "Term")
        .Add("fin_mois", "End month", "Term")
        .Add("fin_annee", "End year", "Term")
        .Add("duree", "Duration", "Term")
        .Add("augmentation_annuelle", "Annual raise", "Term")
        .Add("clause_liberatoire", "Release clause", "Clauses")
        .Add("liberation_relegation", "Release on relegation", "Clauses")
        .Add("liberation_non_qualification_europe", "Release if no Europe", "Clauses")
        .Add("statut", "Role / status", "Clauses")
        .Add("id_equipe", "Team", "Contract", choices: GameService.TeamChoices);

    public static FieldSet League() => new FieldSet()
        .Add("id", "Competition ID", "Identity")
        .Add("nom_championnat", "Name", "Identity")
        .Add("nom_championnat_original", "Original name", "Identity")
        .Add("nom_ligue", "Organisation", "Identity")
        .Add("abreviation_championnat", "Abbreviation", "Identity")
        .Add("abreviation_logo", "Logo abbreviation", "Identity")
        .Add("fake_name", "Fake name", "Identity")
        .Add("president", "President", "Identity")
        .Add("creation", "Founded (year)", "Identity")
        .Add("championnat_pays", "Country", "Settings", choices: GameService.CountryChoicesWithNone)
        .Add("id_continent", "Continent ID", "Settings")
        .Add("division", "Division", "Settings")
        .Add("reputation", "Reputation", "Settings", FieldKind.Slider, 0, 100)
        .Add("type", "Type", "Settings")
        .Add("type_competition", "Competition type", "Settings")
        .Add("sexe", "Gender", "Settings")
        .Add("is_active", "Active", "Settings", FieldKind.Bool)
        .Add("peut_il_etre_joue", "Playable", "Settings", FieldKind.Bool)
        .Add("min_budget", "Minimum budget", "Finance")
        .Add("min_ms", "Minimum payroll", "Finance")
        .Add("competition_associee", "Associated competition", "Settings", choices: GameService.LeagueChoicesWithNone);

    public static FieldSet Rules() => new FieldSet()
        .Add("ligue_id", "Competition ID", "Match")
        .Add("nb_quart_temps", "Quarters", "Match")
        .Add("quart_temps_duree", "Quarter length (min)", "Match")
        .Add("duree_prolongation", "Overtime (min)", "Match")
        .Add("duree_possession", "Shot clock (s)", "Match")
        .Add("ligne_trois_points", "Three-point line (m)", "Match")
        .Add("faute_max", "Max fouls", "Match")
        .Add("nombre_points_victoire", "Points per win", "Standings")
        .Add("nombre_points_defaite", "Points per loss", "Standings")
        .Add("type_classement", "Ranking type", "Standings")
        .Add("nb_joueurs_autorises", "Roster size", "Roster")
        .Add("usa_max", "Max US players", "Roster")
        .Add("etranger_max", "Max foreigners", "Roster")
        .Add("type_salary_cap", "Salary cap type", "Roster")
        .Add("transfers_allowed", "Transfers allowed", "Roster", FieldKind.Bool)
        .Add("loans_allowed", "Loans allowed", "Roster", FieldKind.Bool)
        .Add("trades_allowed", "Trades allowed", "Roster", FieldKind.Bool);
}
