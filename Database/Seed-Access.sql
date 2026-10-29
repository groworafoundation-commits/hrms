-- HRMS menus, roles and rights (run once against HRMSINTER; safe to run again).
-- HRMS menu options live in interface.interface with LEVELCODE = 'HRMS'. PRGNAME holds the in-app route, PICTURE1 the icon name.
SET NOCOUNT ON;

IF NOT EXISTS (SELECT 1 FROM interface.Application WHERE Code = 'HRMS')
    INSERT interface.Application (Code, AppName, AppExeName, Locationcode, flag) VALUES ('HRMS', 'HRMS', 'HrmsApp.exe', 26, 1);

DECLARE @o TABLE (id nvarchar(20), name nvarchar(80), route nvarchar(500), icon nvarchar(100), seq int);
INSERT @o VALUES
 ('H101', 'My Dashboard',    'my',                'home',           1),
 ('H102', 'My Leave',        'my/leave',          'calendar',       2),
 ('H103', 'My Calendar',     'my/calendar',       'calendar-check', 3),
 ('H104', 'My Payslips',     'my/payslips',       'wallet',         4),
 ('H105', 'My Profile',      'my/profile',        'user',           5),
 ('H201', 'Leave Approvals', 'admin/approvals',   'check',         20),
 ('H202', 'Users',           'admin/users',       'users',         21),
 ('H203', 'Roles and Access','admin/roles',       'shield',        22),
 ('H204', 'Menus',           'admin/menus',       'menu',          23);

INSERT interface.interface (OPTIONID, OPTIONNAME, ACTIVESTAT, SEQUENCE, LEVELCODE, VISIBLE, PRGNAME, SUBMENU, Appid, AuthLevel, PICTURE1, ReportPage, htmleditor, paytermspage, bizapirequired, optimize, sendmail)
SELECT id, name, '1', seq, 'HRMS', '1', route, '0', 'HRMS', 0, icon, 0, 0, '', 0, 0, 0 FROM @o o
WHERE NOT EXISTS (SELECT 1 FROM interface.interface i WHERE i.OPTIONID = o.id);

-- Roles: employee self-service, manager, administrator.
INSERT interface.Roledef (roleid, rolename, authlevel, Discount, Transactions)
SELECT r.id, r.name, r.lvl, 0, '0' FROM (VALUES ('HRM-EMP', 'HRMS Employee', 1), ('HRM-MGR', 'HRMS Manager', 5), ('HRM-ADM', 'HRMS Administrator', 10)) r(id, name, lvl)
WHERE NOT EXISTS (SELECT 1 FROM interface.Roledef d WHERE d.roleid = r.id);

-- Rights: (role, option, view, add, modify, delete, print, level)
DECLARE @r TABLE (role nvarchar(16), opt nvarchar(16), v bit, a bit, m bit, d bit, p bit, lvl int);
INSERT @r VALUES
 ('HRM-EMP','H101',1,0,0,0,0,1), ('HRM-EMP','H102',1,1,1,1,0,1), ('HRM-EMP','H103',1,0,0,0,0,1), ('HRM-EMP','H104',1,0,0,0,1,1), ('HRM-EMP','H105',1,0,0,0,0,1),
 ('HRM-MGR','H101',1,0,0,0,0,5), ('HRM-MGR','H102',1,1,1,1,0,5), ('HRM-MGR','H103',1,0,0,0,0,5), ('HRM-MGR','H104',1,0,0,0,1,5), ('HRM-MGR','H105',1,0,0,0,0,5),
 ('HRM-MGR','H201',1,1,1,1,0,5), ('HRM-MGR','137',1,0,0,0,1,5), ('HRM-MGR','210',1,1,1,0,1,5), ('HRM-MGR','162',1,0,0,0,1,5), ('HRM-MGR','164',1,0,0,0,1,5),
 ('HRM-ADM','H101',1,1,1,1,1,10), ('HRM-ADM','H102',1,1,1,1,1,10), ('HRM-ADM','H103',1,1,1,1,1,10), ('HRM-ADM','H104',1,1,1,1,1,10), ('HRM-ADM','H105',1,1,1,1,1,10),
 ('HRM-ADM','H201',1,1,1,1,1,10), ('HRM-ADM','H202',1,1,1,1,1,10), ('HRM-ADM','H203',1,1,1,1,1,10), ('HRM-ADM','H204',1,1,1,1,1,10),
 ('HRM-ADM','137',1,1,1,1,1,10), ('HRM-ADM','207',1,1,1,1,1,10), ('HRM-ADM','208',1,1,1,1,1,10), ('HRM-ADM','209',1,1,1,1,1,10), ('HRM-ADM','210',1,1,1,1,1,10),
 ('HRM-ADM','1012',1,1,1,1,1,10), ('HRM-ADM','1765',1,1,1,1,1,10), ('HRM-ADM','162',1,1,1,1,1,10), ('HRM-ADM','164',1,1,1,1,1,10);

INSERT interface.accessright (roleid, optionid, addd, modifyy, delete1, access, [level], Prints, SinglePush)
SELECT role, opt, a, m, d, v, lvl, p, 0 FROM @r x
WHERE NOT EXISTS (SELECT 1 FROM interface.accessright ar WHERE ar.roleid = x.role AND ar.optionid = x.opt);

SELECT 'options' AS what, COUNT(*) AS n FROM interface.interface WHERE LEVELCODE = 'HRMS'
UNION ALL SELECT 'roles', COUNT(*) FROM interface.Roledef WHERE roleid LIKE 'HRM-%'
UNION ALL SELECT 'rights', COUNT(*) FROM interface.accessright WHERE roleid LIKE 'HRM-%';

-- Approval hierarchy: employee 8 -> administrator 9 -> manager 10 (10 is the leave document level, so a manager's approval is final).
-- Only the HRMS menus carry the hierarchy level; the ERP payroll screens stay at level 10 so entries made there are never left unauthorised.
UPDATE interface.Roledef SET authlevel = CASE roleid WHEN 'HRM-EMP' THEN 8 WHEN 'HRM-ADM' THEN 9 WHEN 'HRM-MGR' THEN 10 END WHERE roleid IN ('HRM-EMP', 'HRM-ADM', 'HRM-MGR');
UPDATE interface.accessright SET [level] = CASE roleid WHEN 'HRM-EMP' THEN 8 WHEN 'HRM-ADM' THEN 9 WHEN 'HRM-MGR' THEN 10 END WHERE roleid IN ('HRM-EMP', 'HRM-ADM', 'HRM-MGR') AND optionid LIKE 'H%';
UPDATE interface.accessright SET [level] = 10 WHERE roleid = 'HRM-MGR' AND optionid NOT LIKE 'H%';
