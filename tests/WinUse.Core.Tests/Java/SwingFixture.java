import java.awt.BorderLayout;
import java.awt.FlowLayout;
import java.awt.event.ActionEvent;
import java.awt.event.ActionListener;
import javax.swing.JButton;
import javax.swing.JCheckBox;
import javax.swing.JCheckBoxMenuItem;
import javax.swing.JComboBox;
import javax.swing.JFrame;
import javax.swing.JLabel;
import javax.swing.JList;
import javax.swing.JMenu;
import javax.swing.JMenuBar;
import javax.swing.JMenuItem;
import javax.swing.JPanel;
import javax.swing.JTabbedPane;
import javax.swing.JTextField;
import javax.swing.SwingUtilities;
import javax.swing.event.ChangeEvent;
import javax.swing.event.ChangeListener;
import javax.swing.event.ListSelectionEvent;
import javax.swing.event.ListSelectionListener;

/**
 * Swing window the winuse integration tests drive through Java Access Bridge. Java 8 syntax on purpose: it also runs on
 * a 32-bit Java 8 JRE. Every interaction is reflected in the status label so a test can verify it from the tree.
 */
public class SwingFixture {
    private static JLabel status;
    private static int increments;

    private static void setStatus(String s) {
        status.setText("status: " + s);
    }

    public static void main(String[] args) {
        SwingUtilities.invokeLater(new Runnable() {
            public void run() { build(); }
        });
    }

    private static void build() {
        final JFrame frame = new JFrame("WinUse Swing Fixture");
        frame.setDefaultCloseOperation(JFrame.EXIT_ON_CLOSE);
        frame.setBounds(100, 100, 640, 480);

        status = new JLabel("status: ready");

        JMenuBar bar = new JMenuBar();
        JMenu file = new JMenu("File");
        JMenuItem newItem = new JMenuItem("New");
        newItem.addActionListener(new ActionListener() {
            public void actionPerformed(ActionEvent e) { setStatus("new"); }
        });
        JMenuItem save = new JMenuItem("Save");
        save.addActionListener(new ActionListener() {
            public void actionPerformed(ActionEvent e) { setStatus("save"); }
        });
        file.add(newItem);
        file.add(save);
        JMenu edit = new JMenu("Edit");
        final JCheckBoxMenuItem wrap = new JCheckBoxMenuItem("Word Wrap");
        wrap.addActionListener(new ActionListener() {
            public void actionPerformed(ActionEvent e) { setStatus("wordwrap " + (wrap.isSelected() ? "on" : "off")); }
        });
        edit.add(wrap);
        bar.add(file);
        bar.add(edit);
        frame.setJMenuBar(bar);

        JPanel top = new JPanel(new FlowLayout(FlowLayout.LEFT));
        JButton increment = new JButton("Increment");
        increment.addActionListener(new ActionListener() {
            public void actionPerformed(ActionEvent e) { increments++; setStatus("increment " + increments); }
        });
        top.add(increment);
        JTextField field = new JTextField("hello", 12);
        field.getAccessibleContext().setAccessibleName("Name");
        top.add(field);
        final JCheckBox check = new JCheckBox("Enabled");
        check.addActionListener(new ActionListener() {
            public void actionPerformed(ActionEvent e) { setStatus("check " + (check.isSelected() ? "on" : "off")); }
        });
        top.add(check);
        final JComboBox<String> combo = new JComboBox<String>(new String[] { "One", "Two", "Three" });
        combo.addActionListener(new ActionListener() {
            public void actionPerformed(ActionEvent e) { setStatus("combo " + combo.getSelectedItem()); }
        });
        top.add(combo);
        frame.add(top, BorderLayout.NORTH);

        final JTabbedPane tabs = new JTabbedPane();
        final JList<String> list = new JList<String>(new String[] { "Alpha", "Beta", "Gamma" });
        list.addListSelectionListener(new ListSelectionListener() {
            public void valueChanged(ListSelectionEvent e) {
                if (!e.getValueIsAdjusting() && list.getSelectedValue() != null) setStatus("list " + list.getSelectedValue());
            }
        });
        tabs.addTab("First", list);

        tabs.addTab("Second", new JLabel("Second page"));
        tabs.addChangeListener(new ChangeListener() {
            public void stateChanged(ChangeEvent e) { setStatus("tab " + tabs.getTitleAt(tabs.getSelectedIndex())); }
        });
        frame.add(tabs, BorderLayout.CENTER);

        // A button eight unnamed panels deep, always visible: the tree must not lose it to depth limits.
        JButton deep = new JButton("Deep");
        deep.addActionListener(new ActionListener() {
            public void actionPerformed(ActionEvent e) { setStatus("deep"); }
        });
        JPanel nested = new JPanel();
        nested.add(deep);
        for (int i = 0; i < 7; i++) {
            JPanel outer = new JPanel();
            outer.add(nested);
            nested = outer;
        }
        frame.add(nested, BorderLayout.EAST);

        frame.add(status, BorderLayout.SOUTH);
        frame.setVisible(true);
    }
}
